using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using KyberDash.Contracts.Repositories;
using KyberDash.Core.Exceptions;
using KyberDash.Data.Collector;
using KyberDash.Domain;
using KyberDash.Domain.Schema;

namespace KyberDash.Application.Tests.Fakes;

/// <summary>
/// Thread-safe, catalog-driven in-memory implementation of all four KyberDash relational store
/// contracts (<see cref="IRelationalStoreWriter"/>, <see cref="IRelationalStoreReader"/>,
/// <see cref="IProjectionStore"/>, and <see cref="IOperationalStore"/>) enforcing primary keys,
/// unique indexes, foreign keys with <c>ON DELETE CASCADE</c>, <c>NOT NULL</c>, catalog
/// <c>CHECK</c> constraints, the three exclusive-pair constraints, and
/// <c>STORE_JSON_TEXT_REFUSED</c> on every <c>TEXT</c> column.
/// </summary>
public class InMemoryRelationalStore :
    IRelationalStoreWriter,
    IRelationalStoreReader,
    IProjectionStore,
    IOperationalStore
{
    private const string ConstraintViolatedCode = "STORE_CONSTRAINT_VIOLATED";
    private const string JsonTextRefusedCode = "STORE_JSON_TEXT_REFUSED";

    private static readonly IReadOnlyDictionary<string, TableMetadata> CatalogMetadata = BuildCatalogMetadata();

    private static readonly (string FirstTable, string SecondTable, string[] KeyColumns)[] ExclusivePairs =
    [
        ("content_part_text", "content_part_withheld", ["span_id", "position"]),
        ("session_context", "session_context_unmeasurable", ["session_id"]),
        ("session_schema", "session_schema_unmeasurable", ["session_id"]),
    ];

    private readonly Lock syncRoot = new();
    private StoreState state = StoreState.CreateEmpty();
    private readonly List<string> readLog = [];
    private readonly List<string> sessionProjectionReadLog = [];
    private readonly List<IReadOnlyList<string>> keyedSessionFigureReads = [];
    private long materializedContentBytes;

    /// <summary>
    /// Names of the <see cref="IRelationalStoreReader"/> methods invoked since the read log was
    /// last cleared, in call order, so tests can assert which reads a code path issued.
    /// </summary>
    public IReadOnlyList<string> ReadLog
    {
        get
        {
            lock (syncRoot)
            {
                return [.. readLog];
            }
        }
    }

    /// <summary>
    /// Names of the session-projection read methods invoked since the read log was last cleared,
    /// in call order: <see cref="ListSessionProjections"/>, <see cref="GetSessionProjection"/>,
    /// and the paged header reads. The read log keeps a path that pages honest about never
    /// falling back to the unfiltered projection list.
    /// </summary>
    public IReadOnlyList<string> SessionProjectionReadLog
    {
        get
        {
            lock (syncRoot)
            {
                return [.. sessionProjectionReadLog];
            }
        }
    }

    /// <summary>
    /// The session keys of every keyed
    /// <see cref="SessionListFigures(IReadOnlyCollection{string}, IModelPricingLookup?)"/> read
    /// since the read log was last cleared, in call order, so a test can assert that a page
    /// priced exactly its own keys.
    /// </summary>
    public IReadOnlyList<IReadOnlyList<string>> KeyedSessionFigureReads
    {
        get
        {
            lock (syncRoot)
            {
                return [.. keyedSessionFigureReads];
            }
        }
    }

    /// <summary>
    /// Total UTF-8 content bytes materialized by record, content-part, and tool-call reads since
    /// the read log was last cleared, so tests can assert how much content a code path pulled
    /// out of the store.
    /// </summary>
    public long MaterializedContentBytes
    {
        get
        {
            lock (syncRoot)
            {
                return materializedContentBytes;
            }
        }
    }

    /// <summary>Clears the instrumented read logs and the materialized content byte counter.</summary>
    public void ClearReadLog()
    {
        lock (syncRoot)
        {
            readLog.Clear();
            sessionProjectionReadLog.Clear();
            keyedSessionFigureReads.Clear();
            materializedContentBytes = 0;
        }
    }

    /// <summary>Records that one <see cref="IRelationalStoreReader"/> method ran. Caller holds <see cref="syncRoot"/>.</summary>
    /// <param name="methodName">Name of the reader method invoked.</param>
    private void NoteRead(string methodName) => readLog.Add(methodName);

    /// <summary>Records that one session-projection read ran. Caller holds <see cref="syncRoot"/>.</summary>
    /// <param name="methodName">Name of the projection-store method invoked.</param>
    private void NoteSessionProjectionRead(string methodName) => sessionProjectionReadLog.Add(methodName);

    /// <summary>
    /// Finds the stored record with <paramref name="spanId"/> together with its content: the
    /// round-trip tests observe ingestion through this after the per-span content-bearing read
    /// was retired from the contract.
    /// </summary>
    /// <param name="spanId">Record span identifier.</param>
    /// <returns>The materialized record, or <see langword="null"/> when no row carries that span.</returns>
    public CollectedRecord? FindRecord(string spanId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(spanId);
        return AllSessionRecordsForMaintenance()
            .FirstOrDefault(record => string.Equals(record.RecordId, spanId, StringComparison.Ordinal));
    }

    /// <summary>Lists the stored content parts of <paramref name="spanId"/>, text included.</summary>
    /// <param name="spanId">Record span identifier.</param>
    public IReadOnlyList<CollectedContentPart> FindRecordParts(string spanId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(spanId);
        return
        [
            .. AllSessionRecordsForMaintenance()
                .Where(record => string.Equals(record.RecordId, spanId, StringComparison.Ordinal))
                .SelectMany(record => record.Parts),
        ];
    }

    /// <summary>Lists the stored content parts of every record linked to <paramref name="sessionKey"/>, text included.</summary>
    /// <param name="sessionKey">Canonical session key.</param>
    public IReadOnlyList<CollectedContentPart> FindSessionParts(string sessionKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionKey);
        return [.. SessionRecordsForRebuild(sessionKey).SelectMany(record => record.Parts)];
    }

    /// <summary>Returns the number of persisted rows in <paramref name="tableName"/>.</summary>
    /// <param name="tableName">Catalog table name.</param>
    public int CountRows(string tableName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tableName);
        TableMetadata metadata = GetTableMetadata(tableName);

        lock (syncRoot)
        {
            return state.GetRows(metadata.Definition.Name).Count;
        }
    }

    /// <summary>Returns all persisted rows in <paramref name="tableName"/> as typed field rows.</summary>
    /// <param name="tableName">Catalog table name.</param>
    public IReadOnlyList<StoredFieldRow> GetTableRows(string tableName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tableName);
        TableMetadata metadata = GetTableMetadata(tableName);

        lock (syncRoot)
        {
            List<object?[]> rows = state.GetRows(metadata.Definition.Name);
            List<StoredFieldRow> result = new(rows.Count);
            foreach (object?[] row in rows)
            {
                result.Add(ToStoredFieldRow(metadata.Definition, row));
            }

            return result;
        }
    }

    /// <summary>
    /// Inserts one row into <paramref name="tableName"/>, enforcing all catalog constraints,
    /// exclusive-pair rules, and JSON text refusal atomically.
    /// </summary>
    /// <param name="tableName">Catalog table name.</param>
    /// <param name="values">Column name/value pairs forming the complete row.</param>
    public void InsertRow(string tableName, params (string ColumnName, object? Value)[] values)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tableName);
        ArgumentNullException.ThrowIfNull(values);
        _ = GetTableMetadata(tableName);

        lock (syncRoot)
        {
            StoreState working = state.Clone();
            InsertRowCore(working, tableName, BuildValueLookup(values));
            state = working;
        }
    }

    /// <summary>
    /// Deletes rows from <paramref name="tableName"/> matching all supplied column predicates,
    /// cascading via <c>ON DELETE CASCADE</c> across all dependent tables atomically.
    /// </summary>
    /// <param name="tableName">Catalog table name.</param>
    /// <param name="predicate">Optional equality predicates identifying rows to remove.</param>
    public int DeleteRows(string tableName, params (string ColumnName, object? Value)[] predicate)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tableName);
        ArgumentNullException.ThrowIfNull(predicate);
        _ = GetTableMetadata(tableName);

        lock (syncRoot)
        {
            StoreState working = state.Clone();
            int removed = DeleteRowsCore(working, tableName, predicate);
            state = working;
            return removed;
        }
    }

    /// <inheritdoc />
    public void WriteBatch(CollectedBatch batch)
    {
        ArgumentNullException.ThrowIfNull(batch);
        ArgumentNullException.ThrowIfNull(batch.Source);
        ArgumentNullException.ThrowIfNull(batch.Sessions);
        ArgumentNullException.ThrowIfNull(batch.Records);
        ArgumentNullException.ThrowIfNull(batch.Problems);

        lock (syncRoot)
        {
            StoreState working = state.Clone();
            string harnessId = batch.Source.HarnessId;
            EnsureHarness(working, harnessId);
            string sourceId = EnsureSource(working, batch.Source);

            HashSet<string> seenSessions = new(StringComparer.Ordinal);
            foreach (CollectedSession session in batch.Sessions)
            {
                ArgumentNullException.ThrowIfNull(session);
                if (session.SessionKey is not null && !seenSessions.Add(session.SessionKey))
                {
                    throw new DashValidationException(
                        ConstraintViolatedCode,
                        $"Duplicate session_id '{session.SessionKey}' in CollectedBatch.Sessions.");
                }

                UpsertSessionCore(working, session);
            }

            foreach (CollectedRecord record in batch.Records)
            {
                ArgumentNullException.ThrowIfNull(record);
                InsertRecordCore(working, sourceId, record);
            }

            HashSet<string> seenProblems = new(StringComparer.Ordinal);
            foreach (CollectedProblem problem in batch.Problems)
            {
                ArgumentNullException.ThrowIfNull(problem);
                if (problem.ProblemKey is not null && !seenProblems.Add(problem.ProblemKey))
                {
                    throw new DashValidationException(
                        ConstraintViolatedCode,
                        $"Duplicate problem_key '{problem.ProblemKey}' in CollectedBatch.Problems.");
                }

                UpsertProblemCore(working, problem);
            }

            state = working;
        }
    }

    /// <inheritdoc />
    public void DeleteSource(string sourceId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceId);

        lock (syncRoot)
        {
            StoreState working = state.Clone();
            _ = DeleteRowsCore(working, "source", [("source_id", sourceId)]);
            state = working;
        }
    }

    /// <inheritdoc />
    public void WriteProblem(CollectedProblem problem)
    {
        ArgumentNullException.ThrowIfNull(problem);

        lock (syncRoot)
        {
            StoreState working = state.Clone();
            UpsertProblemCore(working, problem);
            state = working;
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<string> Harnesses()
    {
        lock (syncRoot)
        {
            NoteRead(nameof(Harnesses));
            TableMetadata meta = GetTableMetadata("harness");
            int col = meta.ColumnIndices["harness_id"];
            return [.. state.GetRows("harness").Select(row => (string)row[col]!).OrderBy(static id => id, StringComparer.Ordinal)];
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<StoredSource> Sources(string? harnessId = null)
    {
        lock (syncRoot)
        {
            NoteRead(nameof(Sources));
            TableMetadata meta = GetTableMetadata("source");
            List<StoredSource> results = [];
            foreach (object?[] row in state.GetRows("source"))
            {
                string rowHarness = GetString(meta, row, "harness_id");
                if (harnessId is not null && !string.Equals(rowHarness, harnessId, StringComparison.Ordinal))
                {
                    continue;
                }

                string sourceId = GetString(meta, row, "source_id");
                SourceKind kind = ParseSourceKind(GetString(meta, row, "source_kind"));
                string locator = GetString(meta, row, "locator");
                results.Add(new StoredSource(sourceId, new CollectedSource(rowHarness, kind, locator)));
            }

            return results;
        }
    }

    /// <inheritdoc />
    public int CountRecords(string? harnessId = null)
    {
        lock (syncRoot)
        {
            NoteRead(nameof(CountRecords));
            if (harnessId is null)
            {
                return state.GetRows("record").Count;
            }

            TableMetadata meta = GetTableMetadata("record");
            int count = 0;
            foreach (object?[] row in state.GetRows("record"))
            {
                if (string.Equals(GetString(meta, row, "harness_id"), harnessId, StringComparison.Ordinal))
                {
                    count++;
                }
            }

            return count;
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<StoredSourceCount> SourceRecordCounts()
    {
        lock (syncRoot)
        {
            NoteRead(nameof(SourceRecordCounts));
            TableMetadata meta = GetTableMetadata("record");
            Dictionary<string, int> counts = new(StringComparer.Ordinal);
            foreach (object?[] row in state.GetRows("record"))
            {
                if (row[meta.ColumnIndices["source_id"]] is not string sourceId)
                {
                    continue;
                }

                counts[sourceId] = counts.TryGetValue(sourceId, out int current) ? current + 1 : 1;
            }

            return
            [
                .. counts
                    .OrderBy(static pair => pair.Key, StringComparer.Ordinal)
                    .Select(static pair => new StoredSourceCount(pair.Key, pair.Value)),
            ];
        }
    }

    /// <inheritdoc />
    public CollectedRecord? RecordContentFree(string spanId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(spanId);

        lock (syncRoot)
        {
            NoteRead(nameof(RecordContentFree));
            object?[]? row = FindRowByKey(state, "record", [("span_id", spanId)]);
            return row is null ? null : MaterializeRecordContentFree(state, row);
        }
    }

    /// <inheritdoc />
    public string? SpanPromptText(string spanId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(spanId);

        lock (syncRoot)
        {
            NoteRead(nameof(SpanPromptText));
            TableMetadata meta = GetTableMetadata("content_part");
            foreach (object?[] row in state.GetRows("content_part")
                .Where(r => string.Equals(GetString(meta, r, "span_id"), spanId, StringComparison.Ordinal))
                .OrderBy(r => GetInt64(meta, r, "position")))
            {
                CollectedContentPart part = MaterializeContentPart(state, row);
                if (part.Bucket is not (ContentBucket.UserPrompt or ContentBucket.UserMessage or ContentBucket.ConversationHistory))
                {
                    continue;
                }

                if (string.IsNullOrWhiteSpace(part.PlainText))
                {
                    continue;
                }

                materializedContentBytes += Encoding.UTF8.GetByteCount(part.PlainText);
                return part.PlainText;
            }

            return null;
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<StoredSessionAttribution> SessionAttributions()
    {
        lock (syncRoot)
        {
            NoteRead(nameof(SessionAttributions));
            TableMetadata linkMeta = GetTableMetadata("record_session_link");
            Dictionary<string, Dictionary<string, int>> countsByKey = new(StringComparer.Ordinal);
            foreach (object?[] linkRow in state.GetRows("record_session_link"))
            {
                string spanId = GetString(linkMeta, linkRow, "span_id");
                object?[]? recordRow = FindRowByKey(state, "record", [("span_id", spanId)]);
                if (recordRow is null)
                {
                    continue;
                }

                string sessionKey = GetString(linkMeta, linkRow, "session_key");
                string harnessId = GetString(GetTableMetadata("record"), recordRow, "harness_id");
                if (!countsByKey.TryGetValue(sessionKey, out Dictionary<string, int>? byHarness))
                {
                    byHarness = new Dictionary<string, int>(StringComparer.Ordinal);
                    countsByKey[sessionKey] = byHarness;
                }

                byHarness[harnessId] = byHarness.TryGetValue(harnessId, out int count) ? count + 1 : 1;
            }

            List<StoredSessionAttribution> results = [];
            foreach (KeyValuePair<string, Dictionary<string, int>> byKey in countsByKey.OrderBy(static kv => kv.Key, StringComparer.Ordinal))
            {
                foreach (KeyValuePair<string, int> byHarness in byKey.Value.OrderBy(static kv => kv.Key, StringComparer.Ordinal))
                {
                    results.Add(new StoredSessionAttribution(byKey.Key, byHarness.Key, byHarness.Value));
                }
            }

            return results;
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<CollectedRecord> SessionRecordsForRebuild(string sessionKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionKey);

        lock (syncRoot)
        {
            NoteRead(nameof(SessionRecordsForRebuild));
            return CollectReaderRecords(sessionKey, null, null);
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<CollectedRecord> AllSessionRecordsForMaintenance()
    {
        lock (syncRoot)
        {
            NoteRead(nameof(AllSessionRecordsForMaintenance));
            return CollectReaderRecords(null, null, null);
        }
    }

    /// <summary>Filters and materializes records for a reader call, counting materialized content bytes. Caller holds <see cref="syncRoot"/>.</summary>
    /// <param name="sessionKey">Optional session key filter.</param>
    /// <param name="harnessId">Optional harness identifier filter.</param>
    /// <param name="sourceId">Optional source identifier filter.</param>
    private List<CollectedRecord> CollectReaderRecords(string? sessionKey, string? harnessId, string? sourceId)
    {
        HashSet<string>? sessionSpans = sessionKey is null ? null : ResolveSessionSpanIds(state, sessionKey);
        TableMetadata meta = GetTableMetadata("record");
        List<CollectedRecord> results = [];

        foreach (object?[] row in state.GetRows("record"))
        {
            string spanId = GetString(meta, row, "span_id");
            if (sessionSpans is not null && !sessionSpans.Contains(spanId))
            {
                continue;
            }

            if (harnessId is not null && !string.Equals(GetString(meta, row, "harness_id"), harnessId, StringComparison.Ordinal))
            {
                continue;
            }

            if (sourceId is not null && !string.Equals(GetString(meta, row, "source_id"), sourceId, StringComparison.Ordinal))
            {
                continue;
            }

            CollectedRecord record = MaterializeRecord(state, row);
            materializedContentBytes += ContentBytesOf(record);
            results.Add(record);
        }

        return results;
    }

    /// <summary>Counts the UTF-8 content bytes a materialized record carries. Caller holds <see cref="syncRoot"/>.</summary>
    /// <param name="record">Materialized record.</param>
    private static long ContentBytesOf(CollectedRecord record)
    {
        long bytes = 0;
        foreach (CollectedContentPart part in record.Parts)
        {
            bytes += ContentBytesOf(part);
        }

        if (record.Tool is not null)
        {
            bytes += ContentBytesOf(record.Tool.Input);
            bytes += ContentBytesOf(record.Tool.Result);
        }

        return bytes;
    }

    /// <summary>Counts the UTF-8 bytes of a content part's plain text payload.</summary>
    /// <param name="part">Materialized content part, when present.</param>
    private static long ContentBytesOf(CollectedContentPart? part) =>
        part?.PlainText is null ? 0 : Encoding.UTF8.GetByteCount(part.PlainText);

    /// <summary>Counts the UTF-8 bytes of a tool-call payload's plain text.</summary>
    /// <param name="content">Tool input or result payload, when present.</param>
    private static long ContentBytesOf(CollectedToolContent? content) =>
        content?.PlainText is null ? 0 : Encoding.UTF8.GetByteCount(content.PlainText);

    /// <inheritdoc />
    public IReadOnlyList<StoredTokenCount> TokenCounts(string? spanId = null, string? sessionKey = null)
    {
        lock (syncRoot)
        {
            NoteRead(nameof(TokenCounts));
            HashSet<string>? allowedSpans = ResolveFilteredSpanIds(state, spanId, sessionKey);
            TableMetadata meta = GetTableMetadata("record_token_count");
            List<StoredTokenCount> results = [];

            foreach (object?[] row in state.GetRows("record_token_count"))
            {
                string rowSpan = GetString(meta, row, "span_id");
                if (allowedSpans is not null && !allowedSpans.Contains(rowSpan))
                {
                    continue;
                }

                results.Add(new StoredTokenCount(
                    rowSpan,
                    ParseTokenClass(GetString(meta, row, "token_class")),
                    GetInt64(meta, row, "token_count")));
            }

            return results;
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<StoredReportedTotal> ReportedTotals(string? spanId = null, string? sessionKey = null)
    {
        lock (syncRoot)
        {
            NoteRead(nameof(ReportedTotals));
            HashSet<string>? allowedSpans = ResolveFilteredSpanIds(state, spanId, sessionKey);
            TableMetadata meta = GetTableMetadata("record_reported_total");
            List<StoredReportedTotal> results = [];

            foreach (object?[] row in state.GetRows("record_reported_total"))
            {
                string rowSpan = GetString(meta, row, "span_id");
                if (allowedSpans is not null && !allowedSpans.Contains(rowSpan))
                {
                    continue;
                }

                ReportedTotalDirection direction = string.Equals(GetString(meta, row, "direction"), "input", StringComparison.Ordinal)
                    ? ReportedTotalDirection.Input
                    : ReportedTotalDirection.Output;
                results.Add(new StoredReportedTotal(rowSpan, direction, GetInt64(meta, row, "token_count")));
            }

            return results;
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<StoredReportedCost> ReportedCosts(string? spanId = null, string? sessionKey = null)
    {
        lock (syncRoot)
        {
            NoteRead(nameof(ReportedCosts));
            HashSet<string>? allowedSpans = ResolveFilteredSpanIds(state, spanId, sessionKey);
            TableMetadata meta = GetTableMetadata("record_reported_cost");
            List<StoredReportedCost> results = [];

            foreach (object?[] row in state.GetRows("record_reported_cost"))
            {
                string rowSpan = GetString(meta, row, "span_id");
                if (allowedSpans is not null && !allowedSpans.Contains(rowSpan))
                {
                    continue;
                }

                results.Add(new StoredReportedCost(
                    rowSpan,
                    GetDouble(meta, row, "amount"),
                    GetString(meta, row, "currency")));
            }

            return results;
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<StoredSkillInvocation> SkillsInvoked(string? spanId = null, string? sessionKey = null)
    {
        lock (syncRoot)
        {
            NoteRead(nameof(SkillsInvoked));
            return CollectSkillInvocations(state, spanId, sessionKey);
        }
    }

    /// <summary>Reads skill invocation rows without logging a reader call. Caller holds <see cref="syncRoot"/>.</summary>
    private static List<StoredSkillInvocation> CollectSkillInvocations(StoreState currentState, string? spanId, string? sessionKey)
    {
        HashSet<string>? allowedSpans = ResolveFilteredSpanIds(currentState, spanId, sessionKey);
        TableMetadata meta = GetTableMetadata("record_skill_invocation");
        List<StoredSkillInvocation> results = [];

        foreach (object?[] row in currentState.GetRows("record_skill_invocation"))
        {
            string rowSpan = GetString(meta, row, "span_id");
            if (allowedSpans is not null && !allowedSpans.Contains(rowSpan))
            {
                continue;
            }

            results.Add(new StoredSkillInvocation(rowSpan, GetString(meta, row, "skill_name")));
        }

        return results;
    }

    /// <inheritdoc />
    public IReadOnlyList<StoredToolCall> ToolCalls(string? spanId = null, string? sessionKey = null)
    {
        lock (syncRoot)
        {
            NoteRead(nameof(ToolCalls));
            HashSet<string>? allowedSpans = ResolveFilteredSpanIds(state, spanId, sessionKey);
            TableMetadata meta = GetTableMetadata("tool_call");
            List<StoredToolCall> results = [];

            foreach (object?[] row in state.GetRows("tool_call"))
            {
                string rowSpan = GetString(meta, row, "span_id");
                if (allowedSpans is not null && !allowedSpans.Contains(rowSpan))
                {
                    continue;
                }

                CollectedToolCall? call = MaterializeToolCall(state, rowSpan);
                if (call is not null)
                {
                    materializedContentBytes += ContentBytesOf(call.Input);
                    materializedContentBytes += ContentBytesOf(call.Result);
                    results.Add(new StoredToolCall(rowSpan, call));
                }
            }

            return results;
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<StoredContentPart> TurnContentParts(
        string sessionKey,
        int? turnIndex = null,
        string? spanId = null,
        string? part = null,
        int budget = 0,
        bool full = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionKey);

        lock (syncRoot)
        {
            NoteRead(nameof(TurnContentParts));

            string? scopedSpan = spanId;
            if (string.IsNullOrEmpty(scopedSpan) && turnIndex.HasValue)
            {
                scopedSpan = ResolveTurnContentSpan(state, sessionKey, turnIndex.Value);
                if (scopedSpan is null)
                {
                    return [];
                }
            }

            HashSet<string>? allowedSpans = ResolveFilteredSpanIds(state, scopedSpan, sessionKey);
            TableMetadata meta = GetTableMetadata("content_part");
            List<StoredContentPart> results = [];

            foreach (object?[] row in state.GetRows("content_part")
                .OrderBy(r => GetString(meta, r, "span_id"), StringComparer.Ordinal)
                .ThenBy(r => (int)GetInt64(meta, r, "position")))
            {
                string rowSpan = GetString(meta, row, "span_id");
                if (allowedSpans is not null && !allowedSpans.Contains(rowSpan))
                {
                    continue;
                }

                if (!string.IsNullOrEmpty(part) && !string.Equals(GetString(meta, row, "bucket"), part, StringComparison.Ordinal))
                {
                    continue;
                }

                results.Add(new StoredContentPart(rowSpan, MaterializeContentPart(state, row)));
            }

            if (full || budget <= 0)
            {
                foreach (StoredContentPart scoped in results)
                {
                    materializedContentBytes += ContentBytesOf(scoped.Part);
                }

                return results;
            }

            List<StoredContentPart> capped = new(results.Count);
            long bytes = 0;
            foreach (StoredContentPart scoped in results)
            {
                capped.Add(scoped);
                long partBytes = ContentBytesOf(scoped.Part);
                bytes += partBytes;
                materializedContentBytes += partBytes;
                if (bytes >= budget)
                {
                    break;
                }
            }

            return capped;
        }
    }

    /// <summary>
    /// Resolves a turn index to the span the turn figures answer for that index, so the content
    /// read is scoped to the same turn the summary already priced.
    /// </summary>
    /// <param name="currentState">Store snapshot to read.</param>
    /// <param name="sessionKey">Canonical session key.</param>
    /// <param name="turnIndex">Zero-based turn index.</param>
    /// <returns>The turn's span identifier, or <see langword="null"/> when the index is outside the session.</returns>
    private static string? ResolveTurnContentSpan(StoreState currentState, string sessionKey, int turnIndex)
    {
        if (turnIndex < 0)
        {
            return null;
        }

        object?[]? sessionRow = FindRowByKey(currentState, "session", [("session_id", sessionKey)]);
        List<CollectedRecord> records = CollectSessionRecordsContentFree(currentState, sessionKey);
        if (sessionRow is null && records.Count == 0)
        {
            return null;
        }

        string harnessId = sessionRow is not null
            ? GetString(GetTableMetadata("session"), sessionRow, "harness_id")
            : records[0].HarnessId;
        IReadOnlyList<ReadTimeTurnSummary> turns = BuildSessionTurnFigures(currentState, sessionKey, records, harnessId, pricing: null);
        return turnIndex < turns.Count ? turns[turnIndex].SpanId : null;
    }

    /// <inheritdoc />
    public IReadOnlyList<StoredToolDefinition> ToolDefinitions(string? spanId = null, string? sessionKey = null)
    {
        lock (syncRoot)
        {
            NoteRead(nameof(ToolDefinitions));
            HashSet<string>? allowedSpans = ResolveFilteredSpanIds(state, spanId, sessionKey);
            TableMetadata meta = GetTableMetadata("tool_definition");
            List<StoredToolDefinition> results = [];

            foreach (object?[] row in state.GetRows("tool_definition")
                .OrderBy(r => GetString(meta, r, "span_id"), StringComparer.Ordinal)
                .ThenBy(r => (int)GetInt64(meta, r, "position")))
            {
                string rowSpan = GetString(meta, row, "span_id");
                if (allowedSpans is not null && !allowedSpans.Contains(rowSpan))
                {
                    continue;
                }

                results.Add(new StoredToolDefinition(rowSpan, MaterializeToolDefinition(state, row)));
            }

            return results;
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<StoredAgentInvocation> AgentInvocations(string? spanId = null, string? sessionKey = null)
    {
        lock (syncRoot)
        {
            NoteRead(nameof(AgentInvocations));
            HashSet<string>? allowedSpans = ResolveFilteredSpanIds(state, spanId, sessionKey);
            TableMetadata meta = GetTableMetadata("agent_invocation");
            List<StoredAgentInvocation> results = [];

            foreach (object?[] row in state.GetRows("agent_invocation"))
            {
                string rowSpan = GetString(meta, row, "span_id");
                if (allowedSpans is not null && !allowedSpans.Contains(rowSpan))
                {
                    continue;
                }

                CollectedAgentInvocation? agent = MaterializeAgentInvocation(state, rowSpan);
                if (agent is not null)
                {
                    results.Add(new StoredAgentInvocation(rowSpan, agent));
                }
            }

            return results;
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<StoredRecordSignals> RecordSignals(string? spanId = null, string? sessionKey = null)
    {
        lock (syncRoot)
        {
            NoteRead(nameof(RecordSignals));
            HashSet<string>? allowedSpans = ResolveFilteredSpanIds(state, spanId, sessionKey);
            HashSet<string> candidateSpans = new(StringComparer.Ordinal);

            CollectSpanIdsFromTable(state, "record_error", candidateSpans);
            CollectSpanIdsFromTable(state, "record_edit_failure", candidateSpans);
            CollectSpanIdsFromTable(state, "record_correction", candidateSpans);
            CollectSpanIdsFromTable(state, "record_declared_context_window", candidateSpans);

            List<StoredRecordSignals> results = [];
            foreach (string candidate in candidateSpans.OrderBy(static id => id, StringComparer.Ordinal))
            {
                if (allowedSpans is not null && !allowedSpans.Contains(candidate))
                {
                    continue;
                }

                results.Add(new StoredRecordSignals(candidate, MaterializeRecordSignals(state, candidate)));
            }

            return results;
        }
    }

    /// <inheritdoc />
    public CollectedSession? GetSession(string sessionKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionKey);

        lock (syncRoot)
        {
            NoteRead(nameof(GetSession));
            object?[]? row = FindRowByKey(state, "session", [("session_id", sessionKey)]);
            return row is null ? null : MaterializeSession(state, row);
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<CollectedSession> Sessions(string? harnessId = null)
    {
        lock (syncRoot)
        {
            NoteRead(nameof(Sessions));
            TableMetadata meta = GetTableMetadata("session");
            List<CollectedSession> results = [];
            foreach (object?[] row in state.GetRows("session"))
            {
                if (harnessId is not null && !string.Equals(GetString(meta, row, "harness_id"), harnessId, StringComparison.Ordinal))
                {
                    continue;
                }

                results.Add(MaterializeSession(state, row));
            }

            return results;
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<StoredSessionModelUsage> SessionModelUsages(string? sessionKey = null)
    {
        lock (syncRoot)
        {
            NoteRead(nameof(SessionModelUsages));
            TableMetadata meta = GetTableMetadata("session_model_usage");
            List<StoredSessionModelUsage> results = [];
            foreach (object?[] row in state.GetRows("session_model_usage")
                .OrderBy(r => GetString(meta, r, "session_id"), StringComparer.Ordinal)
                .ThenBy(r => (int)GetInt64(meta, r, "snapshot_position"))
                .ThenBy(r => GetString(meta, r, "model_id"), StringComparer.Ordinal)
                .ThenBy(r => GetString(meta, r, "token_class"), StringComparer.Ordinal))
            {
                string rowSession = GetString(meta, row, "session_id");
                if (sessionKey is not null && !string.Equals(rowSession, sessionKey, StringComparison.Ordinal))
                {
                    continue;
                }

                results.Add(new StoredSessionModelUsage(
                    rowSession,
                    new CollectedModelUsage(
                        (int)GetInt64(meta, row, "snapshot_position"),
                        GetString(meta, row, "model_id"),
                        ParseTokenClass(GetString(meta, row, "token_class")),
                        GetInt64(meta, row, "token_count"))));
            }

            return results;
        }
    }

    /// <inheritdoc />
    public StoredFieldsView StoredFields(string sessionKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionKey);

        lock (syncRoot)
        {
            NoteRead(nameof(StoredFields));
            return BuildStoredFieldsView(state, sessionKey);
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<SessionListFigure> SessionListFigures(string? harnessId = null, IModelPricingLookup? pricing = null)
    {
        lock (syncRoot)
        {
            NoteRead(nameof(SessionListFigures));
            return BuildSessionListFigures((_, rowHarness) => harnessId is null || string.Equals(rowHarness, harnessId, StringComparison.Ordinal), pricing);
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<SessionListFigure> SessionListFigures(IReadOnlyCollection<string> sessionKeys, IModelPricingLookup? pricing = null)
    {
        ArgumentNullException.ThrowIfNull(sessionKeys);

        lock (syncRoot)
        {
            NoteRead(nameof(SessionListFigures));
            keyedSessionFigureReads.Add([.. sessionKeys]);
            HashSet<string> keys = new(sessionKeys, StringComparer.Ordinal);
            return BuildSessionListFigures((rowKey, _) => keys.Contains(rowKey), pricing);
        }
    }

    /// <summary>
    /// Builds the sessions-list figures of the session rows <paramref name="include"/> selects, in
    /// session-row order, from each row's content-free records. Caller holds
    /// <see cref="syncRoot"/>.
    /// </summary>
    /// <param name="include">Predicate over a session row's key and harness id.</param>
    /// <param name="pricing">Optional pricing lookup overriding default bundled rate tables.</param>
    /// <returns>One figure per selected session row.</returns>
    private List<SessionListFigure> BuildSessionListFigures(Func<string, string, bool> include, IModelPricingLookup? pricing)
    {
        TableMetadata sessionMeta = GetTableMetadata("session");
        List<SessionListFigure> results = [];
        foreach (object?[] row in state.GetRows("session"))
        {
            string sessionKey = GetString(sessionMeta, row, "session_id");
            if (!include(sessionKey, GetString(sessionMeta, row, "harness_id")))
            {
                continue;
            }

            CollectedSession session = MaterializeSession(state, row);
            List<CollectedRecord> records = CollectSessionRecordsContentFree(state, sessionKey);
            results.Add(BuildSessionListFigure(state, session, records, pricing));
        }

        return results;
    }

    /// <inheritdoc />
    public SessionSummaryFigure? SessionSummaryFigures(string sessionKey, IModelPricingLookup? pricing = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionKey);

        lock (syncRoot)
        {
            NoteRead(nameof(SessionSummaryFigures));
            SessionSummaryFigure? summary = BuildSessionFigures(state, sessionKey, CollectSessionRecordsContentFree(state, sessionKey), pricing)?.Summary;
            return summary is null
                ? null
                : summary with { SkillInvocations = CollectSkillInvocations(state, spanId: null, sessionKey) };
        }
    }

    /// <inheritdoc />
    public SessionDetailFigure? SessionDetailFigures(string sessionKey, IModelPricingLookup? pricing = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionKey);

        lock (syncRoot)
        {
            NoteRead(nameof(SessionDetailFigures));
            List<CollectedRecord> records = CollectSessionRecordsContentFree(state, sessionKey);
            SessionFigureSet? figures = BuildSessionFigures(state, sessionKey, records, pricing);
            if (figures is null)
            {
                return null;
            }

            return new SessionDetailFigure(
                figures.Summary with { SkillInvocations = CollectSkillInvocations(state, spanId: null, sessionKey) },
                BuildSessionTurnFigures(state, sessionKey, records, figures.Summary.HarnessId, pricing),
                figures.Tools,
                figures.Requests,
                figures.Servers,
                figures.Reconciliation,
                figures.FirstContextBucket,
                figures.LastContextBucket,
                figures.UnusedRange);
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<CollectedRecord> SessionRecordsContentFree(string sessionKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionKey);

        lock (syncRoot)
        {
            NoteRead(nameof(SessionRecordsContentFree));
            return CollectSessionRecordsContentFree(state, sessionKey);
        }
    }

    /// <inheritdoc />
    public ReadTimeTurnSummary? TurnFigures(string sessionKey, int turnIndex, IModelPricingLookup? pricing = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionKey);
        if (turnIndex < 0)
        {
            return null;
        }

        lock (syncRoot)
        {
            NoteRead(nameof(TurnFigures));
            object?[]? sessionRow = FindRowByKey(state, "session", [("session_id", sessionKey)]);
            List<CollectedRecord> records = CollectSessionRecordsContentFree(state, sessionKey);
            if (sessionRow is null && records.Count == 0)
            {
                return null;
            }

            string harnessId = sessionRow is not null
                ? GetString(GetTableMetadata("session"), sessionRow, "harness_id")
                : records[0].HarnessId;
            IReadOnlyList<ReadTimeTurnSummary> turns = BuildSessionTurnFigures(state, sessionKey, records, harnessId, pricing);
            return turnIndex < turns.Count ? turns[turnIndex] : null;
        }
    }

    /// <inheritdoc />
    public SessionTurnFigureSet? SessionTurnFigures(string sessionKey, IModelPricingLookup? pricing = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionKey);

        lock (syncRoot)
        {
            NoteRead(nameof(SessionTurnFigures));
            object?[]? sessionRow = FindRowByKey(state, "session", [("session_id", sessionKey)]);
            List<CollectedRecord> records = CollectSessionRecordsContentFree(state, sessionKey);
            if (sessionRow is null && records.Count == 0)
            {
                return null;
            }

            string harnessId = sessionRow is not null
                ? GetString(GetTableMetadata("session"), sessionRow, "harness_id")
                : records[0].HarnessId;
            IReadOnlyList<ReadTimeTurnSummary> turns = BuildSessionTurnFigures(state, sessionKey, records, harnessId, pricing);
            ReadTimeContextBucketSummary? firstContextBucket = null;
            ReadTimeContextBucketSummary? lastContextBucket = null;
            if (GetSessionProjectionFromState(state, sessionKey)?.Context?.Turns is { Count: > 0 } contextTurns)
            {
                List<StoredSessionContextTurn> orderedContext = [.. contextTurns.OrderBy(static t => t.Position)];
                List<(int Position, CollectedRecord Record)> turnEntries =
                    ResolveSessionTurnEntries(state, sessionKey, records);
                firstContextBucket = BuildContextBucketSummary(orderedContext[0], turnEntries);
                lastContextBucket = BuildContextBucketSummary(orderedContext[^1], turnEntries);
            }

            return new SessionTurnFigureSet(turns, firstContextBucket, lastContextBucket);
        }
    }

    /// <inheritdoc />
    public virtual SessionRequestFigurePageResult SessionRequestFigures(string sessionKey, int page, int limit)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionKey);
        ArgumentOutOfRangeException.ThrowIfLessThan(page, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);

        lock (syncRoot)
        {
            NoteRead(nameof(SessionRequestFigures));
            List<ReadTimeSessionRequestSummary> rows =
            [
                .. BuildSessionFigures(state, sessionKey, CollectSessionRecordsContentFree(state, sessionKey), null)?.Requests ?? [],
            ];
            return new SessionRequestFigurePageResult(PageOf(rows, page, limit), rows.Count);
        }
    }

    /// <inheritdoc />
    public virtual SessionReconciliationFigurePageResult SessionReconciliationFigures(string sessionKey, int page, int limit)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionKey);
        ArgumentOutOfRangeException.ThrowIfLessThan(page, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);

        lock (syncRoot)
        {
            NoteRead(nameof(SessionReconciliationFigures));
            // The section renders the measurable rows only, as the detail payload's
            // reconciliation rows do: a row whose either match is absent is not rendered.
            List<ReadTimeTurnReconciliation> rows =
            [
                .. (BuildSessionFigures(state, sessionKey, CollectSessionRecordsContentFree(state, sessionKey), null)?.Reconciliation ?? [])
                    .Where(static row => row.InputMatch.HasValue && row.OutputMatch.HasValue),
            ];
            return new SessionReconciliationFigurePageResult(PageOf(rows, page, limit), rows.Count);
        }
    }

    /// <inheritdoc />
    public virtual SessionProblemPageResult SessionProblems(string sessionKey, int page, int limit)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionKey);
        ArgumentOutOfRangeException.ThrowIfLessThan(page, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);

        lock (syncRoot)
        {
            NoteRead(nameof(SessionProblems));
            List<Problem> rows = CollectSessionProblems(state, sessionKey, ResolveSessionSpanIds(state, sessionKey));
            return new SessionProblemPageResult(PageOf(rows, page, limit), rows.Count);
        }
    }

    /// <inheritdoc />
    public virtual IReadOnlyList<StoredRequestPromptPreview> RequestPromptPreviews(string sessionKey, IReadOnlyCollection<string> requestSpanIds)
    {
        ArgumentNullException.ThrowIfNull(requestSpanIds);
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionKey);

        lock (syncRoot)
        {
            NoteRead(nameof(RequestPromptPreviews));
            if (requestSpanIds.Count == 0)
            {
                return [];
            }

            List<CollectedRecord> records = CollectSessionRecordsContentFree(state, sessionKey);
            List<(int Position, CollectedRecord Record)> turnEntries = ResolveSessionTurnEntries(state, sessionKey, records);
            List<StoredRequestPromptPreview> previews = [];
            foreach (string requestSpanId in requestSpanIds.Distinct(StringComparer.Ordinal))
            {
                previews.Add(new StoredRequestPromptPreview(
                    requestSpanId,
                    MaterializeRequestPromptPreview(state, sessionKey, requestSpanId, records, turnEntries)));
            }

            return previews;
        }
    }

    /// <summary>
    /// Reads one request span's prompt preview: its own prompt-bucket parts first, then the parts
    /// of the turns it owns, in turn order — the same rule the detail figures' request rows use —
    /// counting the part text the candidate records' parts materialize.
    /// </summary>
    /// <param name="currentState">Store state to read parts from.</param>
    /// <param name="sessionKey">Owning session key.</param>
    /// <param name="requestSpanId">Request span whose preview is read.</param>
    /// <param name="records">The session's content-free records, for turn ownership.</param>
    /// <param name="turnEntries">The session's turn entries, in turn order.</param>
    /// <returns>The truncated preview, or <see langword="null"/> when the span carries none.</returns>
    private string? MaterializeRequestPromptPreview(
        StoreState currentState,
        string sessionKey,
        string requestSpanId,
        IReadOnlyList<CollectedRecord> records,
        IReadOnlyList<(int Position, CollectedRecord Record)> turnEntries)
    {
        HashSet<string> subtree = CollectDescendantSpanIds(requestSpanId, records);
        List<CollectedRecord> candidates = [];
        CollectedRecord? requestRecord = MaterializeRecordWithParts(currentState, requestSpanId);
        if (requestRecord is not null)
        {
            candidates.Add(requestRecord);
        }

        foreach ((int _, CollectedRecord turnEntry) in turnEntries)
        {
            if (subtree.Contains(turnEntry.RecordId) && turnEntry.RecordId != requestSpanId)
            {
                CollectedRecord? ownedTurn = MaterializeRecordWithParts(currentState, turnEntry.RecordId);
                if (ownedTurn is not null)
                {
                    candidates.Add(ownedTurn);
                }
            }
        }

        foreach (CollectedRecord candidate in candidates)
        {
            foreach (CollectedContentPart part in candidate.Parts)
            {
                materializedContentBytes += ContentBytesOf(part);
            }
        }

        return ExtractPromptPreview(requestRecord, [.. candidates.Skip(requestRecord is null ? 0 : 1)]);
    }

    /// <summary>Materializes one record's scalar columns, tokens, and content parts, when its row exists.</summary>
    /// <param name="currentState">Store state to read from.</param>
    /// <param name="spanId">Record span id.</param>
    /// <returns>The record, or <see langword="null"/> when no row carries the span.</returns>
    private static CollectedRecord? MaterializeRecordWithParts(StoreState currentState, string spanId)
    {
        object?[]? row = FindRowByKey(currentState, "record", [("span_id", spanId)]);
        return row is null ? null : MaterializeRecord(currentState, row);
    }

    /// <summary>Cuts one page of <paramref name="rows"/>, tolerating a page past the end.</summary>
    /// <param name="rows">The rows the read computed, in their own order.</param>
    /// <param name="page">One-based page number.</param>
    /// <param name="limit">Rows on the page.</param>
    /// <returns>The page's rows; empty when the page starts past the end.</returns>
    private static List<T> PageOf<T>(IReadOnlyList<T> rows, int page, int limit)
    {
        if (rows.Count == 0)
        {
            return [];
        }

        long offset = Math.Min((long)(page - 1) * limit, rows.Count);
        return [.. rows.Skip((int)offset).Take(limit)];
    }

    /// <inheritdoc />
    public StoredSessionProjection? GetSessionProjection(string sessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);

        lock (syncRoot)
        {
            NoteSessionProjectionRead(nameof(GetSessionProjection));
            return GetSessionProjectionFromState(state, sessionId);
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<StoredSessionProjection> ListSessionProjections(string? harnessId = null)
    {
        lock (syncRoot)
        {
            NoteSessionProjectionRead(nameof(ListSessionProjections));
            TableMetadata projMeta = GetTableMetadata("session_projection");
            TableMetadata sessionMeta = GetTableMetadata("session");
            List<StoredSessionProjection> results = [];

            foreach (object?[] row in state.GetRows("session_projection")
                .OrderByDescending(r => GetString(projMeta, r, "started_at_utc"), StringComparer.Ordinal))
            {
                string sessionId = GetString(projMeta, row, "session_id");
                if (harnessId is not null)
                {
                    object?[]? sessionRow = FindRowByKey(state, "session", [("session_id", sessionId)]);
                    if (sessionRow is null || !string.Equals(GetString(sessionMeta, sessionRow, "harness_id"), harnessId, StringComparison.Ordinal))
                    {
                        continue;
                    }
                }

                results.Add(MaterializeSessionProjection(state, row));
            }

            return results;
        }
    }

    /// <inheritdoc />
    public SessionHeaderPageResult SessionHeaderPage(string? harnessId, string? search, int page, int limit)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(page, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);

        lock (syncRoot)
        {
            NoteSessionProjectionRead(nameof(SessionHeaderPage));
            List<StoredSessionHeader> scope = SessionProjectionHeadersInScope(state, harnessId);
            Dictionary<string, string?> parentById = ParentLinksOf(scope);
            List<StoredSessionHeader> roots =
            [
                .. scope.Where(header => IsSessionRoot(header, parentById) && SessionHeaderMatches(header, search)),
            ];
            return PageSessionHeaders(roots, page, limit);
        }
    }

    /// <inheritdoc />
    public SessionHeaderPageResult SessionChildHeaderPage(string parentSessionId, int page, int limit)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(parentSessionId);
        ArgumentOutOfRangeException.ThrowIfLessThan(page, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);

        lock (syncRoot)
        {
            NoteSessionProjectionRead(nameof(SessionChildHeaderPage));
            return PageSessionHeaders(AttachableChildren(SessionProjectionHeadersInScope(state, null), parentSessionId), page, limit);
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<SessionChildPreviewEntry> SessionChildPreview(IReadOnlyCollection<string> parentSessionIds, int perParent)
    {
        ArgumentNullException.ThrowIfNull(parentSessionIds);
        ArgumentOutOfRangeException.ThrowIfLessThan(perParent, 1);

        lock (syncRoot)
        {
            NoteSessionProjectionRead(nameof(SessionChildPreview));
            List<StoredSessionHeader> scope = SessionProjectionHeadersInScope(state, null);
            Dictionary<string, string?> parentById = ParentLinksOf(scope);
            HashSet<string> requested = new(parentSessionIds, StringComparer.Ordinal);
            Dictionary<string, List<StoredSessionHeader>> childrenByParent = new(StringComparer.Ordinal);
            foreach (StoredSessionHeader header in scope)
            {
                if (header.ParentSessionId is not null
                    && requested.Contains(header.ParentSessionId)
                    && !IsSessionRoot(header, parentById))
                {
                    if (!childrenByParent.TryGetValue(header.ParentSessionId, out List<StoredSessionHeader>? children))
                    {
                        children = [];
                        childrenByParent[header.ParentSessionId] = children;
                    }

                    children.Add(header);
                }
            }

            // The scope is ordered newest start first and then by session id, so walking it
            // answers the previews in that order.
            List<SessionChildPreviewEntry> entries = [];
            foreach (StoredSessionHeader parent in scope)
            {
                if (!childrenByParent.TryGetValue(parent.SessionId, out List<StoredSessionHeader>? attachable))
                {
                    continue;
                }

                entries.Add(new SessionChildPreviewEntry(
                    parent.SessionId,
                    attachable.Count,
                    [.. attachable
                        .OrderBy(static child => child.StartedAtUtc, StringComparer.Ordinal)
                        .ThenBy(static child => child.SessionId, StringComparer.Ordinal)
                        .Select(static child => child.SessionId)
                        .Take(perParent)]));
            }

            return entries;
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<StoredSessionHeader> SessionHeadersByKeys(IReadOnlyCollection<string> sessionIds)
    {
        ArgumentNullException.ThrowIfNull(sessionIds);

        lock (syncRoot)
        {
            NoteSessionProjectionRead(nameof(SessionHeadersByKeys));
            HashSet<string> keys = new(sessionIds, StringComparer.Ordinal);
            return [.. SessionProjectionHeadersInScope(state, null).Where(header => keys.Contains(header.SessionId))];
        }
    }

    /// <summary>
    /// Reads the projection headers of every session in <paramref name="harnessId"/>'s scope —
    /// every projected session, or those whose session row carries that harness — newest start
    /// first and then by session id, reading no child collection. Caller holds
    /// <see cref="syncRoot"/>.
    /// </summary>
    /// <param name="currentState">Store state to read.</param>
    /// <param name="harnessId">Optional harness filter over the session rows.</param>
    /// <returns>The scoped projection headers, in page order.</returns>
    private static List<StoredSessionHeader> SessionProjectionHeadersInScope(StoreState currentState, string? harnessId)
    {
        TableMetadata projMeta = GetTableMetadata("session_projection");
        TableMetadata sessionMeta = GetTableMetadata("session");
        List<StoredSessionHeader> headers = [];
        foreach (object?[] row in currentState.GetRows("session_projection"))
        {
            if (harnessId is not null)
            {
                object?[]? sessionRow = FindRowByKey(currentState, "session", [("session_id", GetString(projMeta, row, "session_id"))]);
                if (sessionRow is null || !string.Equals(GetString(sessionMeta, sessionRow, "harness_id"), harnessId, StringComparison.Ordinal))
                {
                    continue;
                }
            }

            headers.Add(MaterializeSessionHeader(currentState, row));
        }

        return [.. headers
            .OrderByDescending(static header => header.StartedAtUtc, StringComparer.Ordinal)
            .ThenBy(static header => header.SessionId, StringComparer.Ordinal)];
    }

    /// <summary>
    /// Reads one projection's header — its columns and single-row satellites, and no child
    /// collection.
    /// </summary>
    /// <param name="currentState">Store state to read.</param>
    /// <param name="row">The <c>session_projection</c> row.</param>
    /// <returns>The projection header.</returns>
    private static StoredSessionHeader MaterializeSessionHeader(StoreState currentState, object?[] row)
    {
        TableMetadata meta = GetTableMetadata("session_projection");
        string sessionId = GetString(meta, row, "session_id");
        return new StoredSessionHeader(
            sessionId,
            GetString(meta, row, "label"),
            GetInt64(meta, row, "is_subagent") == 1L,
            (int)GetInt64(meta, row, "span_count"),
            GetString(meta, row, "started_at_utc"),
            GetString(meta, row, "ended_at_utc"),
            ReadOptionalSingleString(currentState, "session_projection_parent", "session_id", sessionId, "parent_session_id"),
            ReadOptionalSingleString(currentState, "session_projection_agent", "session_id", sessionId, "agent_name"),
            FindRowByKey(currentState, "session_projection_included_in_parent", [("session_id", sessionId)]) is not null,
            ReadOptionalSingleString(currentState, "session_context_unmeasurable", "session_id", sessionId, "reason"),
            ReadOptionalSingleString(currentState, "session_schema_unmeasurable", "session_id", sessionId, "reason"));
    }

    /// <summary>Maps every session in a scope to its parent link. A parent that is not in the scope stays named, and the root rule treats it as absent.</summary>
    /// <param name="scope">The scoped projection headers.</param>
    /// <returns>Each scoped session id and its parent session id, or <see langword="null"/>.</returns>
    private static Dictionary<string, string?> ParentLinksOf(IReadOnlyList<StoredSessionHeader> scope) =>
        scope.ToDictionary(static header => header.SessionId, static header => header.ParentSessionId, StringComparer.Ordinal);

    /// <summary>
    /// The root rule: a session is a root unless its parent is a projected session in the scope
    /// and attaching it would form no cycle. Attaching would complete a cycle when the parent's
    /// own chain leads back to this session.
    /// </summary>
    /// <param name="header">The session to classify.</param>
    /// <param name="parentById">Each session in the scope and its parent link.</param>
    /// <returns><see langword="true"/> when the session is a root.</returns>
    private static bool IsSessionRoot(StoredSessionHeader header, IReadOnlyDictionary<string, string?> parentById)
    {
        if (string.IsNullOrEmpty(header.ParentSessionId) || !parentById.ContainsKey(header.ParentSessionId))
        {
            return true;
        }

        HashSet<string> seen = new(StringComparer.Ordinal);
        for (string? cursor = header.ParentSessionId; cursor is not null && seen.Add(cursor); cursor = parentById.TryGetValue(cursor, out string? parent) ? parent : null)
        {
            if (string.Equals(cursor, header.SessionId, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The sessions in a scope that attach to <paramref name="parentSessionId"/> — every projected
    /// session's children that are not roots — oldest start first and then by session id.
    /// </summary>
    /// <param name="scope">The scoped projection headers.</param>
    /// <param name="parentSessionId">The parent session.</param>
    /// <returns>The parent's attachable children.</returns>
    private static List<StoredSessionHeader> AttachableChildren(List<StoredSessionHeader> scope, string parentSessionId)
    {
        Dictionary<string, string?> parentById = ParentLinksOf(scope);
        return
        [
            .. scope
                .Where(header => string.Equals(header.ParentSessionId, parentSessionId, StringComparison.Ordinal) && !IsSessionRoot(header, parentById))
                .OrderBy(static header => header.StartedAtUtc, StringComparer.Ordinal)
                .ThenBy(static header => header.SessionId, StringComparer.Ordinal),
        ];
    }

    /// <summary>
    /// Whether a session's label or session id contains the search text: ASCII letters match
    /// either case and every other character matches itself, which is what the adapter's
    /// <c>LIKE</c> answers.
    /// </summary>
    /// <param name="header">The session to match.</param>
    /// <param name="search">Search text, or <see langword="null"/> for no filter.</param>
    /// <returns><see langword="true"/> when the session matches.</returns>
    private static bool SessionHeaderMatches(StoredSessionHeader header, string? search) =>
        search is null || ContainsAsciiIgnoreCase(header.Label, search) || ContainsAsciiIgnoreCase(header.SessionId, search);

    private static bool ContainsAsciiIgnoreCase(string text, string search)
    {
        for (int start = 0; start + search.Length <= text.Length; start++)
        {
            int matched = 0;
            while (matched < search.Length && FoldAscii(text[start + matched]) == FoldAscii(search[matched]))
            {
                matched++;
            }

            if (matched == search.Length)
            {
                return true;
            }
        }

        return false;
    }

    private static char FoldAscii(char value) => value is >= 'A' and <= 'Z' ? (char)(value + ('a' - 'A')) : value;

    /// <summary>Cuts a page from an ordered header list and reports the list's size.</summary>
    /// <param name="ordered">Headers in page order.</param>
    /// <param name="page">One-based page number.</param>
    /// <param name="limit">Rows on a page.</param>
    /// <returns>The page's headers and the size of <paramref name="ordered"/>.</returns>
    private static SessionHeaderPageResult PageSessionHeaders(List<StoredSessionHeader> ordered, int page, int limit)
    {
        long skip = ((long)page - 1) * limit;
        return new SessionHeaderPageResult([.. ordered.Skip(skip >= ordered.Count ? ordered.Count : (int)skip).Take(limit)], ordered.Count);
    }

    /// <inheritdoc />
    public void ReplaceSessionProjection(StoredSessionProjection projection)
    {
        ArgumentNullException.ThrowIfNull(projection);
        ReplaceSessionProjections([projection]);
    }

    /// <inheritdoc />
    public void ReplaceSessionProjections(IEnumerable<StoredSessionProjection> projections)
    {
        ArgumentNullException.ThrowIfNull(projections);

        lock (syncRoot)
        {
            StoreState working = state.Clone();
            foreach (StoredSessionProjection projection in projections)
            {
                ArgumentNullException.ThrowIfNull(projection);
                ReplaceSessionProjectionCore(working, projection);
            }

            state = working;
        }
    }

    /// <inheritdoc />
    public void DeleteSessionProjection(string sessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);

        lock (syncRoot)
        {
            StoreState working = state.Clone();
            _ = DeleteRowsCore(working, "session_projection", [("session_id", sessionId)]);
            state = working;
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<StoredSessionTurn> GetSessionTurns(string sessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);

        lock (syncRoot)
        {
            return MaterializeSessionTurns(state, sessionId);
        }
    }

    /// <inheritdoc />
    public void ReplaceSessionTurns(string sessionId, IEnumerable<StoredSessionTurn> turns)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentNullException.ThrowIfNull(turns);

        lock (syncRoot)
        {
            StoreState working = state.Clone();
            if (FindRowByKey(working, "session_projection", [("session_id", sessionId)]) is null)
            {
                throw new DashValidationException(
                    ConstraintViolatedCode,
                    $"Foreign key constraint violated on 'session_turn' -> 'session_projection' for session_id '{sessionId}'.");
            }

            _ = DeleteRowsCore(working, "session_turn", [("session_id", sessionId)]);
            foreach (StoredSessionTurn turn in turns)
            {
                ArgumentNullException.ThrowIfNull(turn);
                if (!string.Equals(turn.SessionId, sessionId, StringComparison.Ordinal))
                {
                    throw new DashValidationException(
                        ConstraintViolatedCode,
                        $"StoredSessionTurn.SessionId '{turn.SessionId}' does not match '{sessionId}'.");
                }

                InsertRowCore(
                    working,
                    "session_turn",
                    [
                        ("session_id", turn.SessionId),
                        ("position", turn.Position),
                        ("span_id", turn.SpanId),
                    ]);
            }

            state = working;
        }
    }

    /// <inheritdoc />
    public StoredRun? GetRun(string runId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);

        lock (syncRoot)
        {
            object?[]? row = FindRowByKey(state, "run", [("run_id", runId)]);
            return row is null ? null : MaterializeRun(state, row);
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<StoredRun> ListRuns(string? harnessId = null)
    {
        lock (syncRoot)
        {
            TableMetadata meta = GetTableMetadata("run");
            List<StoredRun> results = [];
            foreach (object?[] row in state.GetRows("run"))
            {
                if (harnessId is not null && !string.Equals(GetString(meta, row, "harness_id"), harnessId, StringComparison.Ordinal))
                {
                    continue;
                }

                results.Add(MaterializeRun(state, row));
            }

            return results;
        }
    }

    /// <inheritdoc />
    public void ReplaceRuns(IEnumerable<StoredRun> runs)
    {
        ArgumentNullException.ThrowIfNull(runs);

        lock (syncRoot)
        {
            StoreState working = state.Clone();
            foreach (StoredRun run in runs)
            {
                ArgumentNullException.ThrowIfNull(run);
                UpsertRunCore(working, run);
            }

            state = working;
        }
    }

    /// <inheritdoc />
    public void DeleteRun(string runId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);

        lock (syncRoot)
        {
            StoreState working = state.Clone();
            _ = DeleteRowsCore(working, "run", [("run_id", runId)]);
            state = working;
        }
    }

    /// <inheritdoc />
    public StoredExecution? GetExecution(string executionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executionId);

        lock (syncRoot)
        {
            object?[]? row = FindRowByKey(state, "execution", [("execution_id", executionId)]);
            return row is null ? null : MaterializeExecution(state, row);
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<StoredExecution> ListExecutionsForRun(string runId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);

        lock (syncRoot)
        {
            TableMetadata meta = GetTableMetadata("execution");
            List<StoredExecution> results = [];
            foreach (object?[] row in state.GetRows("execution"))
            {
                if (string.Equals(GetString(meta, row, "run_id"), runId, StringComparison.Ordinal))
                {
                    results.Add(MaterializeExecution(state, row));
                }
            }

            return results;
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<StoredExecution> ListExecutionsByHarness(string harnessId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(harnessId);

        lock (syncRoot)
        {
            TableMetadata meta = GetTableMetadata("execution");
            List<StoredExecution> results = [];
            foreach (object?[] row in state.GetRows("execution"))
            {
                if (string.Equals(GetString(meta, row, "harness_id"), harnessId, StringComparison.Ordinal))
                {
                    results.Add(MaterializeExecution(state, row));
                }
            }

            return results;
        }
    }

    /// <inheritdoc />
    public void ReplaceExecutions(IEnumerable<StoredExecution> executions)
    {
        ArgumentNullException.ThrowIfNull(executions);

        lock (syncRoot)
        {
            StoreState working = state.Clone();
            List<StoredExecution> items = [];
            foreach (StoredExecution execution in executions)
            {
                ArgumentNullException.ThrowIfNull(execution);
                items.Add(execution);
            }

            foreach (StoredExecution execution in items)
            {
                _ = DeleteRowsCore(working, "execution", [("execution_id", execution.ExecutionId)]);
                InsertRowCore(
                    working,
                    "execution",
                    [
                        ("execution_id", execution.ExecutionId),
                        ("run_id", execution.RunId),
                        ("harness_id", execution.HarnessId),
                        ("is_root", execution.IsRoot ? 1L : 0L),
                    ]);
            }

            foreach (StoredExecution execution in items)
            {
                if (execution.SessionId is not null)
                {
                    InsertRowCore(
                        working,
                        "execution_session",
                        [("execution_id", execution.ExecutionId), ("session_id", execution.SessionId)]);
                }

                if (execution.ParentExecutionId is not null)
                {
                    InsertRowCore(
                        working,
                        "execution_parent",
                        [("execution_id", execution.ExecutionId), ("parent_execution_id", execution.ParentExecutionId)]);
                }

                if (execution.AgentName is not null)
                {
                    InsertRowCore(
                        working,
                        "execution_agent",
                        [("execution_id", execution.ExecutionId), ("agent_name", execution.AgentName)]);
                }

                if (execution.StartedAtUtc is not null || execution.EndedAtUtc is not null)
                {
                    InsertRowCore(
                        working,
                        "execution_bounds",
                        [
                            ("execution_id", execution.ExecutionId),
                            ("started_at_utc", execution.StartedAtUtc),
                            ("ended_at_utc", execution.EndedAtUtc),
                        ]);
                }
            }

            state = working;
        }
    }

    /// <inheritdoc />
    public void DeleteExecutionsForRun(string runId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);

        lock (syncRoot)
        {
            StoreState working = state.Clone();
            _ = DeleteRowsCore(working, "execution", [("run_id", runId)]);
            state = working;
        }
    }

    /// <inheritdoc />
    public StoredHarnessRollup? GetHarnessRollup(string harnessId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(harnessId);

        lock (syncRoot)
        {
            object?[]? row = FindRowByKey(state, "harness_rollup", [("harness_id", harnessId)]);
            return row is null ? null : MaterializeHarnessRollup(state, row);
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<StoredHarnessRollup> ListHarnessRollups()
    {
        lock (syncRoot)
        {
            TableMetadata meta = GetTableMetadata("harness_rollup");
            return
            [
                .. state.GetRows("harness_rollup")
                    .OrderBy(r => GetString(meta, r, "harness_id"), StringComparer.Ordinal)
                    .Select(r => MaterializeHarnessRollup(state, r)),
            ];
        }
    }

    /// <inheritdoc />
    public void ReplaceHarnessRollups(IEnumerable<StoredHarnessRollup> rollups)
    {
        ArgumentNullException.ThrowIfNull(rollups);

        lock (syncRoot)
        {
            StoreState working = state.Clone();
            foreach (StoredHarnessRollup rollup in rollups)
            {
                ArgumentNullException.ThrowIfNull(rollup);
                _ = DeleteRowsCore(working, "harness_rollup", [("harness_id", rollup.HarnessId)]);
                InsertRowCore(
                    working,
                    "harness_rollup",
                    [
                        ("harness_id", rollup.HarnessId),
                        ("session_count", rollup.SessionCount),
                        ("run_count", rollup.RunCount),
                        ("execution_count", rollup.ExecutionCount),
                        ("unknown_window_sessions", rollup.UnknownWindowSessions),
                        ("window_sessions_total", rollup.WindowSessionsTotal),
                    ]);

                if (rollup.Metrics is not null)
                {
                    foreach (StoredHarnessRollupMetric metric in rollup.Metrics)
                    {
                        ArgumentNullException.ThrowIfNull(metric);
                        InsertRowCore(
                            working,
                            "harness_rollup_metric",
                            [
                                ("harness_id", rollup.HarnessId),
                                ("metric", metric.Metric),
                                ("value", metric.Value),
                                ("basis", metric.Basis),
                            ]);
                    }
                }
            }

            state = working;
        }
    }

    /// <inheritdoc />
    public void DeleteHarnessRollup(string harnessId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(harnessId);

        lock (syncRoot)
        {
            StoreState working = state.Clone();
            _ = DeleteRowsCore(working, "harness_rollup", [("harness_id", harnessId)]);
            state = working;
        }
    }

    /// <inheritdoc />
    public StoredFinding? GetFinding(string findingId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(findingId);

        lock (syncRoot)
        {
            object?[]? row = FindRowByKey(state, "finding", [("finding_id", findingId)]);
            return row is null ? null : MaterializeFinding(state, row);
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<StoredFinding> ListFindings(string? runId = null, string? sessionId = null)
    {
        lock (syncRoot)
        {
            TableMetadata meta = GetTableMetadata("finding");
            List<StoredFinding> results = [];

            foreach (object?[] row in state.GetRows("finding")
                .OrderByDescending(r => GetDouble(meta, r, "rank_score")))
            {
                StoredFinding finding = MaterializeFinding(state, row);
                if (runId is not null && !string.Equals(finding.RunId, runId, StringComparison.Ordinal))
                {
                    continue;
                }

                if (sessionId is not null && !string.Equals(finding.SessionId, sessionId, StringComparison.Ordinal))
                {
                    continue;
                }

                results.Add(finding);
            }

            return results;
        }
    }

    /// <inheritdoc />
    public void ReplaceFindings(IEnumerable<StoredFinding> findings)
    {
        ArgumentNullException.ThrowIfNull(findings);

        lock (syncRoot)
        {
            StoreState working = state.Clone();
            foreach (StoredFinding finding in findings)
            {
                ArgumentNullException.ThrowIfNull(finding);
                _ = DeleteRowsCore(working, "finding", [("finding_id", finding.FindingId)]);
                InsertRowCore(
                    working,
                    "finding",
                    [
                        ("finding_id", finding.FindingId),
                        ("detector_id", finding.DetectorId),
                        ("title", finding.Title),
                        ("mechanism", finding.Mechanism),
                        ("confidence", finding.Confidence),
                        ("recommendation", finding.Recommendation),
                        ("outcome_risk_caveat", finding.OutcomeRiskCaveat),
                        ("rank_score", finding.RankScore),
                    ]);

                if (finding.EstimatedWasteTokens.HasValue)
                {
                    InsertRowCore(
                        working,
                        "finding_estimated_waste",
                        [("finding_id", finding.FindingId), ("tokens", finding.EstimatedWasteTokens.Value)]);
                }

                if (finding.ErrorBar is not null)
                {
                    InsertRowCore(
                        working,
                        "finding_error_bar",
                        [
                            ("finding_id", finding.FindingId),
                            ("lower", (double)finding.ErrorBar.Lower),
                            ("upper", (double)finding.ErrorBar.Upper),
                        ]);
                }

                if (finding.EvidenceLinks is not null)
                {
                    for (int index = 0; index < finding.EvidenceLinks.Count; index++)
                    {
                        FindingEvidenceLink link = finding.EvidenceLinks[index];
                        ArgumentNullException.ThrowIfNull(link);
                        InsertRowCore(
                            working,
                            "finding_evidence_link",
                            [
                                ("finding_id", finding.FindingId),
                                ("position", index),
                                ("span_id", link.SpanId),
                                ("turn_index", link.TurnIndex),
                                ("description", link.Description),
                            ]);
                    }
                }

                if (finding.RunId is not null)
                {
                    InsertRowCore(
                        working,
                        "finding_run",
                        [("finding_id", finding.FindingId), ("run_id", finding.RunId)]);
                }

                if (finding.SessionId is not null)
                {
                    InsertRowCore(
                        working,
                        "finding_session",
                        [("finding_id", finding.FindingId), ("session_id", finding.SessionId)]);
                }

                if (finding.MeasurementClass is not null)
                {
                    InsertRowCore(
                        working,
                        "finding_measurement_class",
                        [("finding_id", finding.FindingId), ("measurement_class", finding.MeasurementClass)]);
                }

                if (finding.ConfidenceBasis is not null)
                {
                    InsertRowCore(
                        working,
                        "finding_confidence_basis",
                        [("finding_id", finding.FindingId), ("confidence_basis", finding.ConfidenceBasis)]);
                }

                if (finding.WhatWouldRaiseIt is not null)
                {
                    InsertRowCore(
                        working,
                        "finding_what_would_raise_it",
                        [("finding_id", finding.FindingId), ("text", finding.WhatWouldRaiseIt)]);
                }

                if (finding.CompactionHazard is not null)
                {
                    InsertRowCore(
                        working,
                        "finding_compaction_hazard",
                        [
                            ("finding_id", finding.FindingId),
                            ("context_limit", finding.CompactionHazard.ContextLimit),
                            ("context_limit_source", finding.CompactionHazard.ContextLimitSource),
                        ]);
                }
            }

            state = working;
        }
    }

    /// <inheritdoc />
    public void DeleteFindingsForSession(string sessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);

        lock (syncRoot)
        {
            StoreState working = state.Clone();
            TableMetadata sessionMeta = GetTableMetadata("finding_session");
            List<string> ids =
            [
                .. working.GetRows("finding_session")
                    .Where(row => string.Equals(GetString(sessionMeta, row, "session_id"), sessionId, StringComparison.Ordinal))
                    .Select(row => GetString(sessionMeta, row, "finding_id")),
            ];

            foreach (string findingId in ids)
            {
                _ = DeleteRowsCore(working, "finding", [("finding_id", findingId)]);
            }

            state = working;
        }
    }

    /// <inheritdoc />
    public StoredPrediction? GetPrediction(string predictionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(predictionId);

        lock (syncRoot)
        {
            object?[]? row = FindRowByKey(state, "prediction", [("prediction_id", predictionId)]);
            return row is null ? null : MaterializePrediction(state, row);
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<StoredPrediction> ListPredictions(string? runId = null, string? findingId = null)
    {
        lock (syncRoot)
        {
            TableMetadata meta = GetTableMetadata("prediction");
            List<StoredPrediction> results = [];

            foreach (object?[] row in state.GetRows("prediction"))
            {
                if (runId is not null && !string.Equals(GetString(meta, row, "run_id"), runId, StringComparison.Ordinal))
                {
                    continue;
                }

                if (findingId is not null && !string.Equals(GetString(meta, row, "finding_id"), findingId, StringComparison.Ordinal))
                {
                    continue;
                }

                results.Add(MaterializePrediction(state, row));
            }

            return results;
        }
    }

    /// <inheritdoc />
    public void ReplacePrediction(StoredPrediction prediction)
    {
        ArgumentNullException.ThrowIfNull(prediction);

        lock (syncRoot)
        {
            StoreState working = state.Clone();
            _ = DeleteRowsCore(working, "prediction", [("prediction_id", prediction.PredictionId)]);
            InsertRowCore(
                working,
                "prediction",
                [
                    ("prediction_id", prediction.PredictionId),
                    ("finding_id", prediction.FindingId),
                    ("run_id", prediction.RunId),
                    ("predicted_waste_tokens", prediction.PredictedWasteTokens),
                    ("confidence", prediction.Confidence),
                    ("confidence_tier", prediction.ConfidenceTier),
                    ("created_at_utc", prediction.CreatedAtUtc),
                ]);

            if (prediction.ComparisonRunId is not null)
            {
                InsertRowCore(
                    working,
                    "prediction_comparison",
                    [("prediction_id", prediction.PredictionId), ("comparison_run_id", prediction.ComparisonRunId)]);
            }

            if (prediction.ObservedDeltaTokens.HasValue)
            {
                InsertRowCore(
                    working,
                    "prediction_observed_delta",
                    [("prediction_id", prediction.PredictionId), ("tokens", prediction.ObservedDeltaTokens.Value)]);
            }

            if (prediction.CalibrationScore.HasValue)
            {
                InsertRowCore(
                    working,
                    "prediction_calibration",
                    [("prediction_id", prediction.PredictionId), ("score", prediction.CalibrationScore.Value)]);
            }

            state = working;
        }
    }

    /// <inheritdoc />
    public void DeletePrediction(string predictionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(predictionId);

        lock (syncRoot)
        {
            StoreState working = state.Clone();
            _ = DeleteRowsCore(working, "prediction", [("prediction_id", predictionId)]);
            state = working;
        }
    }

    /// <inheritdoc />
    public SettingsView ReadSettings()
    {
        lock (syncRoot)
        {
            bool folderImport = ReadSingletonBool(state, "operator_setting_folder_import", "scheduled", false);
            bool jobsPaused = ReadSingletonBool(state, "operator_setting_jobs", "paused", false);
            int cadence = (int)ReadSingletonInt64(state, "operator_setting_refresh_cadence", "minutes", 5);
            bool receiverHosted = ReadSingletonBool(state, "operator_setting_receiver", "hosted", false);
            int port = (int)ReadSingletonInt64(state, "operator_setting_dashboard", "port", 4747);
            return new SettingsView(folderImport, jobsPaused, cadence, receiverHosted, port);
        }
    }

    /// <inheritdoc />
    public void SaveSettings(SettingsView settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        lock (syncRoot)
        {
            StoreState working = state.Clone();
            ReplaceSingleton(working, "operator_setting_folder_import", ("scheduled", settings.FolderImportScheduled ? 1L : 0L));
            ReplaceSingleton(working, "operator_setting_jobs", ("paused", settings.JobsPaused ? 1L : 0L));
            ReplaceSingleton(working, "operator_setting_refresh_cadence", ("minutes", (long)settings.RefreshCadenceMinutes));
            ReplaceSingleton(working, "operator_setting_receiver", ("hosted", settings.ReceiverHosted ? 1L : 0L));
            ReplaceSingleton(working, "operator_setting_dashboard", ("port", (long)settings.DashboardPort));
            state = working;
        }
    }

    /// <inheritdoc />
    public StoreStateView? GetStoreState()
    {
        lock (syncRoot)
        {
            object?[]? row = FindSingletonRow(state, "store_state");
            if (row is null)
            {
                return null;
            }

            TableMetadata meta = GetTableMetadata("store_state");
            return new StoreStateView(
                (int)GetInt64(meta, row, "schema_version"),
                (int)GetInt64(meta, row, "detector_version"));
        }
    }

    /// <inheritdoc />
    public void SetStoreState(StoreStateView state)
    {
        ArgumentNullException.ThrowIfNull(state);

        lock (syncRoot)
        {
            StoreState working = this.state.Clone();
            ReplaceSingleton(
                working,
                "store_state",
                ("schema_version", (long)state.SchemaVersion),
                ("detector_version", (long)state.DetectorVersion));
            this.state = working;
        }
    }

    /// <inheritdoc />
    public long GetWriteMarker()
    {
        lock (syncRoot)
        {
            return ReadSingletonInt64(state, "store_write_marker", "records_write_seq", 0L);
        }
    }

    /// <inheritdoc />
    public void SetWriteMarker(long recordsWriteSeq)
    {
        lock (syncRoot)
        {
            StoreState working = state.Clone();
            ReplaceSingleton(working, "store_write_marker", ("records_write_seq", recordsWriteSeq));
            state = working;
        }
    }

    /// <inheritdoc />
    public string? GetLastCleanAtUtc()
    {
        lock (syncRoot)
        {
            return ReadSingletonString(state, "store_clean_stamp", "last_clean_at_utc");
        }
    }

    /// <inheritdoc />
    public void SetLastCleanAtUtc(string lastCleanAtUtc)
    {
        lock (syncRoot)
        {
            StoreState working = state.Clone();
            ReplaceSingleton(working, "store_clean_stamp", ("last_clean_at_utc", lastCleanAtUtc));
            state = working;
        }
    }

    /// <inheritdoc />
    public int? GetContentRetentionDays()
    {
        lock (syncRoot)
        {
            object?[]? row = FindSingletonRow(state, "content_retention");
            if (row is null)
            {
                return null;
            }

            TableMetadata meta = GetTableMetadata("content_retention");
            return (int)GetInt64(meta, row, "retention_days");
        }
    }

    /// <inheritdoc />
    public void SetContentRetentionDays(int retentionDays)
    {
        lock (syncRoot)
        {
            StoreState working = state.Clone();
            ReplaceSingleton(working, "content_retention", ("retention_days", (long)retentionDays));
            state = working;
        }
    }

    /// <inheritdoc />
    public string? GetContentPurgeMarkUtc()
    {
        lock (syncRoot)
        {
            return ReadSingletonString(state, "content_purge_mark", "purged_through_utc");
        }
    }

    /// <inheritdoc />
    public void SetContentPurgeMarkUtc(string purgedThroughUtc)
    {
        lock (syncRoot)
        {
            StoreState working = state.Clone();
            ReplaceSingleton(working, "content_purge_mark", ("purged_through_utc", purgedThroughUtc));
            state = working;
        }
    }

    /// <inheritdoc />
    public ProjectionStateView? GetProjectionState()
    {
        lock (syncRoot)
        {
            object?[]? row = FindSingletonRow(state, "projection_state");
            if (row is null)
            {
                return null;
            }

            TableMetadata meta = GetTableMetadata("projection_state");
            return new ProjectionStateView(
                (int)GetInt64(meta, row, "projection_schema_version"),
                GetInt64(meta, row, "records_marker"));
        }
    }

    /// <inheritdoc />
    public void SetProjectionState(ProjectionStateView state)
    {
        ArgumentNullException.ThrowIfNull(state);

        lock (syncRoot)
        {
            StoreState working = this.state.Clone();
            ReplaceSingleton(
                working,
                "projection_state",
                ("projection_schema_version", (long)state.ProjectionSchemaVersion),
                ("records_marker", state.RecordsMarker));
            this.state = working;
        }
    }

    /// <inheritdoc />
    public DirtyProjectionState? GetProjectionDirtyState()
    {
        lock (syncRoot)
        {
            object?[]? rangeRow = FindSingletonRow(state, "projection_dirty_range");
            if (rangeRow is null)
            {
                return null;
            }

            TableMetadata rangeMeta = GetTableMetadata("projection_dirty_range");
            TableMetadata sessionMeta = GetTableMetadata("projection_dirty_session");
            List<string> keys =
            [
                .. state.GetRows("projection_dirty_session")
                    .Select(row => GetString(sessionMeta, row, "session_id")),
            ];

            bool isWildcard = keys.Count == 0;
            return new DirtyProjectionState(
                GetInt64(rangeMeta, rangeRow, "from_seq"),
                GetInt64(rangeMeta, rangeRow, "to_seq"),
                isWildcard ? null : keys,
                isWildcard);
        }
    }

    /// <inheritdoc />
    public void SetProjectionDirtyState(DirtyProjectionState state)
    {
        ArgumentNullException.ThrowIfNull(state);

        lock (syncRoot)
        {
            StoreState working = this.state.Clone();
            ReplaceSingleton(
                working,
                "projection_dirty_range",
                ("from_seq", state.FromSeq),
                ("to_seq", state.ToSeq));

            _ = DeleteRowsCore(working, "projection_dirty_session", []);
            if (!state.IsWildcard && state.Keys is not null)
            {
                foreach (string key in state.Keys)
                {
                    InsertRowCore(working, "projection_dirty_session", [("session_id", key)]);
                }
            }

            this.state = working;
        }
    }

    /// <inheritdoc />
    public void ClearProjectionDirtyState()
    {
        lock (syncRoot)
        {
            StoreState working = state.Clone();
            _ = DeleteRowsCore(working, "projection_dirty_range", []);
            _ = DeleteRowsCore(working, "projection_dirty_session", []);
            state = working;
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<SessionKeyAlias> ListSessionKeyAliases(string? sessionId = null)
    {
        lock (syncRoot)
        {
            TableMetadata meta = GetTableMetadata("session_key_alias");
            List<SessionKeyAlias> results = [];

            foreach (object?[] row in state.GetRows("session_key_alias"))
            {
                string rowSession = GetString(meta, row, "session_id");
                if (sessionId is not null && !string.Equals(rowSession, sessionId, StringComparison.Ordinal))
                {
                    continue;
                }

                results.Add(new SessionKeyAlias(GetString(meta, row, "alias_key"), rowSession));
            }

            return results;
        }
    }

    /// <inheritdoc />
    public void ReplaceSessionKeyAliases(IEnumerable<SessionKeyAlias> aliases)
    {
        ArgumentNullException.ThrowIfNull(aliases);

        lock (syncRoot)
        {
            StoreState working = state.Clone();
            _ = DeleteRowsCore(working, "session_key_alias", []);
            foreach (SessionKeyAlias alias in aliases)
            {
                ArgumentNullException.ThrowIfNull(alias);
                InsertRowCore(
                    working,
                    "session_key_alias",
                    [("alias_key", alias.AliasKey), ("session_id", alias.SessionId)]);
            }

            state = working;
        }
    }

    /// <inheritdoc />
    public void UpsertRefreshRun(RefreshRunRow row)
    {
        ArgumentNullException.ThrowIfNull(row);

        lock (syncRoot)
        {
            StoreState working = state.Clone();
            _ = DeleteRowsCore(working, "refresh_run", [("refresh_run_id", row.Id)]);
            InsertRowCore(
                working,
                "refresh_run",
                [
                    ("refresh_run_id", row.Id),
                    ("started_at_utc", row.StartedAt),
                    ("status", row.Status),
                    ("pid", (long)row.Pid),
                    ("trigger", row.Trigger),
                ]);

            if (row.CompletedAt is not null)
            {
                InsertRowCore(
                    working,
                    "refresh_run_completion",
                    [("refresh_run_id", row.Id), ("completed_at_utc", row.CompletedAt)]);
            }

            if (row.Summary is not null)
            {
                InsertRowCore(
                    working,
                    "refresh_run_summary",
                    [("refresh_run_id", row.Id), ("summary", row.Summary)]);
            }

            if (row.HistoryWeeks.HasValue)
            {
                InsertRowCore(
                    working,
                    "refresh_run_history_window",
                    [("refresh_run_id", row.Id), ("history_weeks", (long)row.HistoryWeeks.Value)]);
            }

            state = working;
        }
    }

    /// <inheritdoc />
    public RefreshRunRow? GetRefreshRun(string refreshRunId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(refreshRunId);

        lock (syncRoot)
        {
            object?[]? row = FindRowByKey(state, "refresh_run", [("refresh_run_id", refreshRunId)]);
            return row is null ? null : MaterializeRefreshRun(state, row);
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<RefreshRunRow> ListRefreshRuns(int limit = 20)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);

        lock (syncRoot)
        {
            TableMetadata meta = GetTableMetadata("refresh_run");
            return
            [
                .. state.GetRows("refresh_run")
                    .OrderByDescending(row => GetString(meta, row, "started_at_utc"), StringComparer.Ordinal)
                    .Take(limit)
                    .Select(row => MaterializeRefreshRun(state, row)),
            ];
        }
    }

    /// <inheritdoc />
    public SourceCheckpoint? GetSourceCheckpoint(string harnessId, string sourceKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(harnessId);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceKey);

        lock (syncRoot)
        {
            object?[]? row = FindRowByKey(
                state,
                "source_checkpoint",
                [("harness_id", harnessId), ("source_key", sourceKey)]);
            return row is null ? null : MaterializeSourceCheckpoint(state, row);
        }
    }

    /// <inheritdoc />
    public void UpsertSourceCheckpoint(SourceCheckpoint checkpoint)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);

        lock (syncRoot)
        {
            StoreState working = state.Clone();
            _ = DeleteRowsCore(
                working,
                "source_checkpoint",
                [("harness_id", checkpoint.HarnessId), ("source_key", checkpoint.SourceKey)]);

            InsertRowCore(
                working,
                "source_checkpoint",
                [
                    ("harness_id", checkpoint.HarnessId),
                    ("source_key", checkpoint.SourceKey),
                    ("provider_id", checkpoint.ProviderId),
                    ("parser_id", checkpoint.ParserId),
                    ("parser_contract_version", checkpoint.ParserContractVersion),
                    ("format", checkpoint.Format),
                    ("source_root_label", checkpoint.SourceRootLabel),
                    ("revision_token", checkpoint.RevisionToken),
                    ("covered_from_utc", checkpoint.CoveredFromUtc),
                    ("covered_through_utc", checkpoint.CoveredThroughUtc),
                    ("last_attempt_utc", checkpoint.LastAttemptUtc),
                    ("last_status", checkpoint.LastStatus),
                    ("unit_count", checkpoint.UnitCount),
                    ("record_count", checkpoint.RecordCount),
                ]);

            if (checkpoint.LastSuccessUtc is not null)
            {
                InsertRowCore(
                    working,
                    "source_checkpoint_success",
                    [
                        ("harness_id", checkpoint.HarnessId),
                        ("source_key", checkpoint.SourceKey),
                        ("last_success_utc", checkpoint.LastSuccessUtc),
                    ]);
            }

            if (checkpoint.LastErrorCode is not null)
            {
                InsertRowCore(
                    working,
                    "source_checkpoint_error",
                    [
                        ("harness_id", checkpoint.HarnessId),
                        ("source_key", checkpoint.SourceKey),
                        ("last_error_code", checkpoint.LastErrorCode),
                    ]);
            }

            state = working;
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<SourceCheckpoint> ListSourceCheckpoints(string? harnessId = null)
    {
        lock (syncRoot)
        {
            TableMetadata meta = GetTableMetadata("source_checkpoint");
            List<SourceCheckpoint> results = [];

            foreach (object?[] row in state.GetRows("source_checkpoint"))
            {
                if (harnessId is not null && !string.Equals(GetString(meta, row, "harness_id"), harnessId, StringComparison.Ordinal))
                {
                    continue;
                }

                results.Add(MaterializeSourceCheckpoint(state, row));
            }

            return results;
        }
    }

    /// <inheritdoc />
    public StoredIngestProblem? GetIngestProblem(long problemId)
    {
        lock (syncRoot)
        {
            object?[]? row = FindRowByKey(state, "ingest_problem", [("problem_id", problemId)]);
            return row is null ? null : MaterializeIngestProblem(state, row);
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<StoredIngestProblem> ListIngestProblems(
        string? harnessId = null,
        string? spanId = null,
        int? limit = null,
        int? offset = null)
    {
        if (limit.HasValue)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(limit.Value);
        }

        if (offset.HasValue)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(offset.Value);
        }

        lock (syncRoot)
        {
            TableMetadata meta = GetTableMetadata("ingest_problem");
            IEnumerable<StoredIngestProblem> query = state.GetRows("ingest_problem")
                .OrderBy(r => GetInt64(meta, r, "problem_id"))
                .Select(r => MaterializeIngestProblem(state, r))
                .Where(p => harnessId is null || string.Equals(p.Problem.HarnessId, harnessId, StringComparison.Ordinal))
                .Where(p => spanId is null || string.Equals(p.Problem.SpanId, spanId, StringComparison.Ordinal));

            if (offset.HasValue && offset.Value > 0)
            {
                query = query.Skip(offset.Value);
            }

            if (limit.HasValue)
            {
                query = query.Take(limit.Value);
            }

            return [.. query];
        }
    }

    /// <inheritdoc />
    public int CountIngestProblems(string? harnessId = null)
    {
        lock (syncRoot)
        {
            if (harnessId is null)
            {
                return state.GetRows("ingest_problem").Count;
            }

            TableMetadata harnessMeta = GetTableMetadata("ingest_problem_harness");
            int count = 0;
            foreach (object?[] row in state.GetRows("ingest_problem_harness"))
            {
                if (string.Equals(GetString(harnessMeta, row, "harness_id"), harnessId, StringComparison.Ordinal))
                {
                    count++;
                }
            }

            return count;
        }
    }

    /// <inheritdoc />
    public void PutQuarantinedSpan(StoredQuarantinedSpan span)
    {
        ArgumentNullException.ThrowIfNull(span);
        ArgumentNullException.ThrowIfNull(span.ObservedNamespaces);

        lock (syncRoot)
        {
            StoreState working = state.Clone();
            _ = DeleteRowsCore(working, "quarantined_span", [("span_id", span.SpanId)]);
            InsertRowCore(
                working,
                "quarantined_span",
                [("span_id", span.SpanId), ("reason", span.Reason)]);

            foreach (string ns in span.ObservedNamespaces)
            {
                InsertRowCore(
                    working,
                    "quarantined_span_namespace",
                    [("span_id", span.SpanId), ("namespace_name", ns)]);
            }

            if (span.Source is not null)
            {
                InsertRowCore(
                    working,
                    "quarantined_span_source",
                    [("span_id", span.SpanId), ("source", span.Source)]);
            }

            if (span.Name is not null)
            {
                InsertRowCore(
                    working,
                    "quarantined_span_name",
                    [("span_id", span.SpanId), ("name", span.Name)]);
            }

            if (span.TimestampUtc is not null)
            {
                InsertRowCore(
                    working,
                    "quarantined_span_timestamp",
                    [("span_id", span.SpanId), ("timestamp_utc", span.TimestampUtc)]);
            }

            if (span.HarnessId is not null)
            {
                InsertRowCore(
                    working,
                    "quarantined_span_harness",
                    [("span_id", span.SpanId), ("harness_id", span.HarnessId)]);
            }

            state = working;
        }
    }

    /// <inheritdoc />
    public StoredQuarantinedSpan? GetQuarantinedSpan(string spanId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(spanId);

        lock (syncRoot)
        {
            object?[]? row = FindRowByKey(state, "quarantined_span", [("span_id", spanId)]);
            return row is null ? null : MaterializeQuarantinedSpan(state, row);
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<StoredQuarantinedSpan> ListQuarantinedSpans(
        string? harnessId = null,
        int? limit = null,
        int? offset = null)
    {
        if (limit.HasValue)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(limit.Value);
        }

        if (offset.HasValue)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(offset.Value);
        }

        lock (syncRoot)
        {
            IEnumerable<StoredQuarantinedSpan> query = state.GetRows("quarantined_span")
                .Select(row => MaterializeQuarantinedSpan(state, row))
                .Where(item => harnessId is null || string.Equals(item.HarnessId, harnessId, StringComparison.Ordinal));

            if (offset.HasValue && offset.Value > 0)
            {
                query = query.Skip(offset.Value);
            }

            if (limit.HasValue)
            {
                query = query.Take(limit.Value);
            }

            return [.. query];
        }
    }

    /// <inheritdoc />
    public int CountQuarantinedSpans(string? harnessId = null)
    {
        lock (syncRoot)
        {
            if (harnessId is null)
            {
                return state.GetRows("quarantined_span").Count;
            }

            TableMetadata meta = GetTableMetadata("quarantined_span_harness");
            int count = 0;
            foreach (object?[] row in state.GetRows("quarantined_span_harness"))
            {
                if (string.Equals(GetString(meta, row, "harness_id"), harnessId, StringComparison.Ordinal))
                {
                    count++;
                }
            }

            return count;
        }
    }

    /// <inheritdoc />
    public long? GetCachedTokenCount(string hash)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hash);

        lock (syncRoot)
        {
            object?[]? row = FindRowByKey(state, "token_cache", [("hash", hash)]);
            if (row is null)
            {
                return null;
            }

            TableMetadata meta = GetTableMetadata("token_cache");
            return GetInt64(meta, row, "token_count");
        }
    }

    /// <inheritdoc />
    public void PutCachedTokenCounts(IEnumerable<StoredTokenCacheEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);

        lock (syncRoot)
        {
            StoreState working = state.Clone();
            foreach (StoredTokenCacheEntry entry in entries)
            {
                ArgumentNullException.ThrowIfNull(entry);
                _ = DeleteRowsCore(working, "token_cache", [("hash", entry.Hash)]);
                InsertRowCore(
                    working,
                    "token_cache",
                    [("hash", entry.Hash), ("token_count", entry.TokenCount)]);

                if (entry.ModelId is not null)
                {
                    InsertRowCore(
                        working,
                        "token_cache_model",
                        [("hash", entry.Hash), ("model_id", entry.ModelId)]);
                }
            }

            state = working;
        }
    }

    /// <inheritdoc />
    public StoredModelCatalogSnapshot GetModelContextWindowCatalog()
    {
        lock (syncRoot)
        {
            string status = ReadSingletonString(state, "context_window_catalog_state", "status") ?? "empty";

            TableMetadata vendorMeta = GetTableMetadata("model_context_window_catalog_vendor");
            TableMetadata refreshMeta = GetTableMetadata("model_context_window_catalog_vendor_refresh");
            TableMetadata errorMeta = GetTableMetadata("model_context_window_catalog_vendor_error");
            List<StoredModelCatalogVendorStatus> vendors = [];
            foreach (object?[] row in state.GetRows("model_context_window_catalog_vendor"))
            {
                string vendor = GetString(vendorMeta, row, "vendor");
                object?[]? refreshRow = FindRowByKey(state, "model_context_window_catalog_vendor_refresh", [("vendor", vendor)]);
                object?[]? errorRow = FindRowByKey(state, "model_context_window_catalog_vendor_error", [("vendor", vendor)]);
                vendors.Add(new StoredModelCatalogVendorStatus(
                    vendor,
                    GetString(vendorMeta, row, "status"),
                    refreshRow is null ? null : GetString(refreshMeta, refreshRow, "last_refresh_at_utc"),
                    errorRow is null ? null : GetString(errorMeta, errorRow, "last_error")));
            }

            TableMetadata entryMeta = GetTableMetadata("model_context_window_catalog");
            TableMetadata revisionMeta = GetTableMetadata("model_context_window_source_revision");
            List<StoredModelContextWindowEntry> entries = [];
            foreach (object?[] row in state.GetRows("model_context_window_catalog"))
            {
                string lookupId = GetString(entryMeta, row, "lookup_id");
                object?[]? revRow = FindRowByKey(state, "model_context_window_source_revision", [("lookup_id", lookupId)]);
                entries.Add(new StoredModelContextWindowEntry(
                    lookupId,
                    GetString(entryMeta, row, "canonical_model_id"),
                    (int)GetInt64(entryMeta, row, "context_window"),
                    GetString(entryMeta, row, "vendor"),
                    new Uri(GetString(entryMeta, row, "documentation_url"), UriKind.RelativeOrAbsolute),
                    GetString(entryMeta, row, "retrieved_at_utc"),
                    revRow is null ? null : GetString(revisionMeta, revRow, "source_revision")));
            }

            return new StoredModelCatalogSnapshot(status, vendors, entries);
        }
    }

    /// <inheritdoc />
    public void ReplaceModelContextWindowCatalog(StoredModelCatalogSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(snapshot.Vendors);
        ArgumentNullException.ThrowIfNull(snapshot.Entries);

        lock (syncRoot)
        {
            StoreState working = state.Clone();
            ReplaceSingleton(working, "context_window_catalog_state", ("status", snapshot.Status));

            _ = DeleteRowsCore(working, "model_context_window_catalog_vendor", []);
            foreach (StoredModelCatalogVendorStatus vendor in snapshot.Vendors)
            {
                ArgumentNullException.ThrowIfNull(vendor);
                InsertRowCore(
                    working,
                    "model_context_window_catalog_vendor",
                    [("vendor", vendor.Vendor), ("status", vendor.Status)]);

                if (vendor.LastRefreshAtUtc is not null)
                {
                    InsertRowCore(
                        working,
                        "model_context_window_catalog_vendor_refresh",
                        [("vendor", vendor.Vendor), ("last_refresh_at_utc", vendor.LastRefreshAtUtc)]);
                }

                if (vendor.LastError is not null)
                {
                    InsertRowCore(
                        working,
                        "model_context_window_catalog_vendor_error",
                        [("vendor", vendor.Vendor), ("last_error", vendor.LastError)]);
                }
            }

            _ = DeleteRowsCore(working, "model_context_window_catalog", []);
            foreach (StoredModelContextWindowEntry entry in snapshot.Entries)
            {
                ArgumentNullException.ThrowIfNull(entry);
                InsertRowCore(
                    working,
                    "model_context_window_catalog",
                    [
                        ("lookup_id", entry.LookupId),
                        ("canonical_model_id", entry.CanonicalModelId),
                        ("context_window", (long)entry.ContextWindow),
                        ("vendor", entry.Vendor),
                        ("documentation_url", entry.DocumentationUrl?.ToString()),
                        ("retrieved_at_utc", entry.RetrievedAtUtc),
                    ]);

                if (entry.SourceRevision is not null)
                {
                    InsertRowCore(
                        working,
                        "model_context_window_source_revision",
                        [("lookup_id", entry.LookupId), ("source_revision", entry.SourceRevision)]);
                }
            }

            state = working;
        }
    }

    /// <inheritdoc />
    public void PutMetricPoints(IEnumerable<StoredMetricPoint> points)
    {
        ArgumentNullException.ThrowIfNull(points);

        lock (syncRoot)
        {
            StoreState working = state.Clone();
            TableMetadata meta = GetTableMetadata("metric_point");

            foreach (StoredMetricPoint point in points)
            {
                ArgumentNullException.ThrowIfNull(point);
                object?[]? existing = FindRowByKey(working, "metric_point", [("point_key", point.PointKey)]);
                long pointId = existing is not null ? GetInt64(meta, existing, "point_id") : working.NextPointId++;
                if (existing is not null)
                {
                    _ = DeleteRowsCore(working, "metric_point", [("point_id", pointId)]);
                }

                InsertRowCore(
                    working,
                    "metric_point",
                    [
                        ("point_id", pointId),
                        ("point_key", point.PointKey),
                        ("name", point.Name),
                        ("kind", point.Kind),
                    ]);

                if (point.Unit is not null)
                {
                    InsertRowCore(working, "metric_point_unit", [("point_id", pointId), ("unit", point.Unit)]);
                }

                if (point.AggregationTemporality.HasValue)
                {
                    InsertRowCore(
                        working,
                        "metric_point_temporality",
                        [("point_id", pointId), ("aggregation_temporality", (long)point.AggregationTemporality.Value)]);
                }

                if (point.TimeUnixNano is not null)
                {
                    InsertRowCore(
                        working,
                        "metric_point_time",
                        [("point_id", pointId), ("time_unix_nano", point.TimeUnixNano)]);
                }

                if (point.StartTimeUnixNano is not null)
                {
                    InsertRowCore(
                        working,
                        "metric_point_start_time",
                        [("point_id", pointId), ("start_time_unix_nano", point.StartTimeUnixNano)]);
                }

                if (point.TimestampUtc is not null)
                {
                    InsertRowCore(
                        working,
                        "metric_point_timestamp",
                        [("point_id", pointId), ("timestamp_utc", point.TimestampUtc)]);
                }

                if (point.Value.HasValue)
                {
                    InsertRowCore(working, "metric_point_value", [("point_id", pointId), ("value", point.Value.Value)]);
                }

                if (point.PointCount.HasValue)
                {
                    InsertRowCore(
                        working,
                        "metric_point_count",
                        [("point_id", pointId), ("point_count", point.PointCount.Value)]);
                }

                if (point.PointSum.HasValue)
                {
                    InsertRowCore(
                        working,
                        "metric_point_sum",
                        [("point_id", pointId), ("point_sum", point.PointSum.Value)]);
                }

                if (point.Source is not null)
                {
                    InsertRowCore(
                        working,
                        "metric_point_source",
                        [("point_id", pointId), ("source", point.Source)]);
                }
            }

            state = working;
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<StoredMetricPoint> ListMetricPoints(string? name = null, int? limit = null)
    {
        if (limit.HasValue)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(limit.Value);
        }

        lock (syncRoot)
        {
            TableMetadata meta = GetTableMetadata("metric_point");
            IEnumerable<StoredMetricPoint> query = state.GetRows("metric_point")
                .Where(row => name is null || string.Equals(GetString(meta, row, "name"), name, StringComparison.Ordinal))
                .Select(row => MaterializeMetricPoint(state, row));

            if (limit.HasValue)
            {
                query = query.Take(limit.Value);
            }

            return [.. query];
        }
    }

    /// <inheritdoc />
    public void RecordIngest(string source, int count, string timestampUtc)
    {
        lock (syncRoot)
        {
            StoreState working = state.Clone();
            long ingestId = working.NextIngestId++;
            InsertRowCore(
                working,
                "ingest_log",
                [
                    ("ingest_id", ingestId),
                    ("source", source),
                    ("record_count", (long)count),
                    ("timestamp_utc", timestampUtc),
                ]);
            state = working;
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<IngestLogEntry> ListIngestLog(string? source = null)
    {
        lock (syncRoot)
        {
            TableMetadata meta = GetTableMetadata("ingest_log");
            List<IngestLogEntry> results = [];

            foreach (object?[] row in state.GetRows("ingest_log"))
            {
                string rowSource = GetString(meta, row, "source");
                if (source is not null && !string.Equals(rowSource, source, StringComparison.Ordinal))
                {
                    continue;
                }

                results.Add(new IngestLogEntry(
                    (int)GetInt64(meta, row, "ingest_id"),
                    rowSource,
                    (int)GetInt64(meta, row, "record_count"),
                    GetString(meta, row, "timestamp_utc")));
            }

            return results;
        }
    }

    /// <inheritdoc />
    public void PutRecordProvenance(IEnumerable<RecordProvenanceRow> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);

        lock (syncRoot)
        {
            StoreState working = state.Clone();
            foreach (RecordProvenanceRow row in rows)
            {
                ArgumentNullException.ThrowIfNull(row);
                _ = DeleteRowsCore(working, "record_provenance", [("span_id", row.SpanId)]);
                InsertRowCore(
                    working,
                    "record_provenance",
                    [
                        ("span_id", row.SpanId),
                        ("harness_id", row.HarnessId),
                        ("source_key", row.SourceKey),
                        ("source_revision", row.SourceRevision),
                        ("parser_version", row.ParserVersion),
                        ("imported_at_utc", row.ImportedAtUtc),
                        ("location_token", row.LocationToken),
                    ]);

                if (row.NativeSessionId is not null)
                {
                    InsertRowCore(
                        working,
                        "record_provenance_native_session",
                        [("span_id", row.SpanId), ("native_session_id", row.NativeSessionId)]);
                }

                if (row.NativeRecordId is not null)
                {
                    InsertRowCore(
                        working,
                        "record_provenance_native_record",
                        [("span_id", row.SpanId), ("native_record_id", row.NativeRecordId)]);
                }
            }

            state = working;
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<RecordProvenanceRow> ListRecordProvenance(string harnessId, string sourceKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(harnessId);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceKey);

        lock (syncRoot)
        {
            TableMetadata meta = GetTableMetadata("record_provenance");
            TableMetadata sessionMeta = GetTableMetadata("record_provenance_native_session");
            TableMetadata recordMeta = GetTableMetadata("record_provenance_native_record");
            List<RecordProvenanceRow> results = [];

            foreach (object?[] row in state.GetRows("record_provenance"))
            {
                if (!string.Equals(GetString(meta, row, "harness_id"), harnessId, StringComparison.Ordinal) ||
                    !string.Equals(GetString(meta, row, "source_key"), sourceKey, StringComparison.Ordinal))
                {
                    continue;
                }

                string spanId = GetString(meta, row, "span_id");
                object?[]? natSess = FindRowByKey(state, "record_provenance_native_session", [("span_id", spanId)]);
                object?[]? natRec = FindRowByKey(state, "record_provenance_native_record", [("span_id", spanId)]);
                results.Add(new RecordProvenanceRow(
                    spanId,
                    harnessId,
                    sourceKey,
                    natSess is null ? null : GetString(sessionMeta, natSess, "native_session_id"),
                    natRec is null ? null : GetString(recordMeta, natRec, "native_record_id"),
                    GetString(meta, row, "source_revision"),
                    GetString(meta, row, "parser_version"),
                    GetString(meta, row, "imported_at_utc"),
                    GetString(meta, row, "location_token")));
            }

            return results;
        }
    }

    private static void EnsureHarness(StoreState working, string harnessId)
    {
        if (harnessId is not null && FindRowByKey(working, "harness", [("harness_id", harnessId)]) is not null)
        {
            return;
        }

        InsertRowCore(working, "harness", [("harness_id", harnessId)]);
    }

    private static string EnsureSource(StoreState working, CollectedSource source)
    {
        string kindText = FormatSourceKind(source.Kind);
        object?[]? existing = FindRowByKey(
            working,
            "source",
            [("harness_id", source.HarnessId), ("source_kind", kindText), ("locator", source.Locator)]);
        if (existing is not null)
        {
            return GetString(GetTableMetadata("source"), existing, "source_id");
        }

        string sourceId = ComputeSourceId(source.HarnessId, kindText, source.Locator);
        InsertRowCore(
            working,
            "source",
            [
                ("source_id", sourceId),
                ("harness_id", source.HarnessId),
                ("source_kind", kindText),
                ("locator", source.Locator),
            ]);
        return sourceId;
    }

    private static string ComputeSourceId(string harnessId, string sourceKind, string locator)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{harnessId}\0{sourceKind}\0{locator}"));
        return $"src-{Convert.ToHexStringLower(hash)[..16]}";
    }

    private static void UpsertSessionCore(StoreState working, CollectedSession session)
    {
        TableMetadata sessionMeta = GetTableMetadata("session");
        object?[]? existing = session.SessionKey is null
            ? null
            : FindRowByKey(working, "session", [("session_id", session.SessionKey)]);

        if (existing is null)
        {
            InsertRowCore(
                working,
                "session",
                [
                    ("session_id", session.SessionKey),
                    ("harness_id", session.HarnessId),
                    ("native_session_id", session.NativeSessionId),
                ]);
        }
        else
        {
            object?[] replacement = ValidateAndNormalizeRow(
                working,
                sessionMeta,
                [
                    ("session_id", session.SessionKey),
                    ("harness_id", session.HarnessId),
                    ("native_session_id", session.NativeSessionId),
                ],
                skipPkDuplicateCheck: true,
                existingRowForUniqueCheck: existing);
            Array.Copy(replacement, existing, replacement.Length);

            _ = DeleteRowsCore(working, "session_workspace", [("session_id", session.SessionKey)]);
            _ = DeleteRowsCore(working, "session_repository", [("session_id", session.SessionKey)]);
            _ = DeleteRowsCore(working, "session_branch", [("session_id", session.SessionKey)]);
            _ = DeleteRowsCore(working, "session_title", [("session_id", session.SessionKey)]);
            _ = DeleteRowsCore(working, "session_model_usage", [("session_id", session.SessionKey)]);
        }

        if (session.Workspace is not null)
        {
            if (session.Workspace.WorkingDirectory is not null)
            {
                InsertRowCore(
                    working,
                    "session_workspace",
                    [("session_id", session.SessionKey), ("working_directory", session.Workspace.WorkingDirectory)]);
            }

            if (session.Workspace.Repository is not null)
            {
                InsertRowCore(
                    working,
                    "session_repository",
                    [("session_id", session.SessionKey), ("repo_name", session.Workspace.Repository)]);
            }

            if (session.Workspace.Branch is not null)
            {
                InsertRowCore(
                    working,
                    "session_branch",
                    [("session_id", session.SessionKey), ("branch_name", session.Workspace.Branch)]);
            }
        }

        if (session.Title is not null)
        {
            InsertRowCore(
                working,
                "session_title",
                [("session_id", session.SessionKey), ("title", session.Title)]);
        }

        if (session.ReportedUsage is not null)
        {
            foreach (CollectedModelUsage usage in session.ReportedUsage)
            {
                ArgumentNullException.ThrowIfNull(usage);
                InsertRowCore(
                    working,
                    "session_model_usage",
                    [
                        ("session_id", session.SessionKey),
                        ("snapshot_position", (long)usage.SnapshotPosition),
                        ("model_id", usage.Model),
                        ("token_class", FormatTokenClass(usage.TokenClass)),
                        ("token_count", usage.Count),
                    ]);
            }
        }
    }

    private static void InsertRecordCore(StoreState working, string sourceId, CollectedRecord record)
    {
        InsertRowCore(
            working,
            "record",
            [
                ("span_id", record.RecordId),
                ("source_id", sourceId),
                ("harness_id", record.HarnessId),
                ("op", FormatRecordOp(record.Op)),
                ("kind", FormatRecordKind(record.Kind)),
                ("name", record.Name),
                ("started_at_utc", FormatTimestamp(record.StartedAt)),
                ("status", FormatRecordStatus(record.Status)),
            ]);

        if (record.SessionKey is not null)
        {
            InsertRowCore(working, "record_session_link", [("span_id", record.RecordId), ("session_key", record.SessionKey)]);
        }

        if (record.ParentRecordId is not null)
        {
            InsertRowCore(working, "record_parent", [("span_id", record.RecordId), ("parent_span_id", record.ParentRecordId)]);
        }

        if (record.TraceId is not null)
        {
            InsertRowCore(working, "record_trace", [("span_id", record.RecordId), ("trace_id", record.TraceId)]);
        }

        if (record.Model is not null)
        {
            InsertRowCore(working, "record_model", [("span_id", record.RecordId), ("model_id", record.Model)]);
        }

        if (record.EndedAt.HasValue)
        {
            InsertRowCore(working, "record_end", [("span_id", record.RecordId), ("ended_at_utc", FormatTimestamp(record.EndedAt.Value))]);
        }

        if (record.ReportedDurationMs.HasValue)
        {
            InsertRowCore(
                working,
                "record_reported_duration",
                [("span_id", record.RecordId), ("duration_ms", record.ReportedDurationMs.Value)]);
        }

        if (record.Tokens is not null)
        {
            foreach (CollectedTokenCount tokenCount in record.Tokens)
            {
                ArgumentNullException.ThrowIfNull(tokenCount);
                InsertRowCore(
                    working,
                    "record_token_count",
                    [
                        ("span_id", record.RecordId),
                        ("token_class", FormatTokenClass(tokenCount.TokenClass)),
                        ("token_count", tokenCount.Count),
                    ]);
            }
        }

        if (record.ReportedInputTokens.HasValue)
        {
            InsertRowCore(
                working,
                "record_reported_total",
                [("span_id", record.RecordId), ("direction", "input"), ("token_count", record.ReportedInputTokens.Value)]);
        }

        if (record.ReportedOutputTokens.HasValue)
        {
            InsertRowCore(
                working,
                "record_reported_total",
                [("span_id", record.RecordId), ("direction", "output"), ("token_count", record.ReportedOutputTokens.Value)]);
        }

        if (record.ReportedCost is not null)
        {
            InsertRowCore(
                working,
                "record_reported_cost",
                [
                    ("span_id", record.RecordId),
                    ("amount", record.ReportedCost.Amount),
                    ("currency", record.ReportedCost.Currency),
                ]);
        }

        if (record.SkillsInvoked is not null)
        {
            foreach (string skillName in record.SkillsInvoked)
            {
                InsertRowCore(
                    working,
                    "record_skill_invocation",
                    [("span_id", record.RecordId), ("skill_name", skillName)]);
            }
        }

        if (record.AgentInstanceId is not null)
        {
            InsertRowCore(
                working,
                "record_agent_instance",
                [("span_id", record.RecordId), ("agent_instance_id", record.AgentInstanceId)]);
        }

        if (record.Signals is null)
        {
            throw new DashValidationException(ConstraintViolatedCode, "CollectedRecord.Signals cannot be null.");
        }

        if (record.Signals.ErrorMessage is not null)
        {
            InsertRowCore(working, "record_error", [("span_id", record.RecordId), ("message", record.Signals.ErrorMessage)]);
        }

        if (record.Signals.EditFailures.HasValue)
        {
            InsertRowCore(
                working,
                "record_edit_failure",
                [("span_id", record.RecordId), ("failure_count", record.Signals.EditFailures.Value)]);
        }

        if (record.Signals.CorrectionRule is not null)
        {
            InsertRowCore(working, "record_correction", [("span_id", record.RecordId)]);
            InsertRowCore(
                working,
                "record_correction_rule",
                [("span_id", record.RecordId), ("correction_rule", record.Signals.CorrectionRule)]);
        }

        if (record.Signals.DeclaredContextWindow.HasValue)
        {
            InsertRowCore(
                working,
                "record_declared_context_window",
                [("span_id", record.RecordId), ("declared_context_window", record.Signals.DeclaredContextWindow.Value)]);
        }

        InsertRecordParts(working, record);
        InsertRecordToolDefinitions(working, record);
        InsertRecordToolCall(working, record);
        InsertRecordAgentInvocation(working, record);
    }

    private static void InsertRecordParts(StoreState working, CollectedRecord record)
    {
        if (record.Parts is null)
        {
            return;
        }

        foreach (CollectedContentPart part in record.Parts)
        {
            ArgumentNullException.ThrowIfNull(part);
            InsertRowCore(
                working,
                "content_part",
                [
                    ("span_id", record.RecordId),
                    ("position", (long)part.Position),
                    ("bucket", FormatContentBucket(part.Bucket)),
                ]);

            if (part.PlainText is not null)
            {
                InsertRowCore(
                    working,
                    "content_part_text",
                    [("span_id", record.RecordId), ("position", (long)part.Position), ("part_text", part.PlainText)]);
            }

            if (part.Withheld is not null)
            {
                InsertRowCore(
                    working,
                    "content_part_withheld",
                    [
                        ("span_id", record.RecordId),
                        ("position", (long)part.Position),
                        ("utf8_bytes", part.Withheld.Utf8Bytes),
                        ("characters", part.Withheld.Characters),
                        ("measured_tokens", part.Withheld.MeasuredTokens),
                    ]);
            }

            if (part.ReportedTokens.HasValue)
            {
                InsertRowCore(
                    working,
                    "content_part_reported_tokens",
                    [("span_id", record.RecordId), ("position", (long)part.Position), ("token_count", part.ReportedTokens.Value)]);
            }

            if (part.Server is not null)
            {
                InsertRowCore(
                    working,
                    "content_part_server",
                    [("span_id", record.RecordId), ("position", (long)part.Position), ("server_name", part.Server)]);
            }

            if (part.Role is not null)
            {
                InsertRowCore(
                    working,
                    "content_part_role",
                    [("span_id", record.RecordId), ("position", (long)part.Position), ("role_name", part.Role)]);
            }

            if (part.Resident.HasValue)
            {
                InsertRowCore(
                    working,
                    "content_part_residency",
                    [
                        ("span_id", record.RecordId),
                        ("position", (long)part.Position),
                        ("is_resident", part.Resident.Value ? 1L : 0L),
                    ]);
            }

            if (part.OriginalUtf8BytesWhenTruncated.HasValue)
            {
                InsertRowCore(
                    working,
                    "content_part_truncation",
                    [
                        ("span_id", record.RecordId),
                        ("position", (long)part.Position),
                        ("original_utf8_bytes", part.OriginalUtf8BytesWhenTruncated.Value),
                    ]);
            }
        }
    }

    private static void InsertRecordToolDefinitions(StoreState working, CollectedRecord record)
    {
        if (record.ToolDefinitions is null)
        {
            return;
        }

        foreach (CollectedToolDefinition definition in record.ToolDefinitions)
        {
            ArgumentNullException.ThrowIfNull(definition);
            InsertRowCore(
                working,
                "tool_definition",
                [
                    ("span_id", record.RecordId),
                    ("position", (long)definition.Position),
                    ("tool_name", definition.ToolName),
                    ("schema_tokens", definition.SchemaTokens),
                ]);

            if (definition.Server is not null)
            {
                InsertRowCore(
                    working,
                    "tool_definition_server",
                    [
                        ("span_id", record.RecordId),
                        ("position", (long)definition.Position),
                        ("server_name", definition.Server),
                    ]);
            }
        }
    }

    private static void InsertRecordToolCall(StoreState working, CollectedRecord record)
    {
        if (record.Tool is null)
        {
            return;
        }

        CollectedToolCall tool = record.Tool;
        InsertRowCore(
            working,
            "tool_call",
            [
                ("span_id", record.RecordId),
                ("call_id", tool.CallId),
                ("tool_name", tool.ToolName),
            ]);

        if (tool.McpServerName is not null || tool.McpToolName is not null)
        {
            InsertRowCore(
                working,
                "tool_call_mcp",
                [
                    ("span_id", record.RecordId),
                    ("server_name", tool.McpServerName),
                    ("server_tool_name", tool.McpToolName),
                ]);
        }

        if (tool.Input is not null)
        {
            InsertRowCore(
                working,
                "tool_call_input_size",
                [
                    ("span_id", record.RecordId),
                    ("utf8_bytes", tool.Input.Utf8Bytes),
                    ("characters", tool.Input.Characters),
                ]);

            if (tool.Input.Sha256Fingerprint is not null)
            {
                InsertRowCore(
                    working,
                    "tool_call_input_fingerprint",
                    [("span_id", record.RecordId), ("sha256", tool.Input.Sha256Fingerprint)]);
            }

            if (tool.Input.PlainText is not null)
            {
                InsertRowCore(
                    working,
                    "tool_call_input_text",
                    [("span_id", record.RecordId), ("text", tool.Input.PlainText)]);
            }

            if (tool.Input.OriginalUtf8BytesWhenTruncated.HasValue)
            {
                InsertRowCore(
                    working,
                    "tool_call_input_truncation",
                    [("span_id", record.RecordId), ("original_utf8_bytes", tool.Input.OriginalUtf8BytesWhenTruncated.Value)]);
            }
        }

        if (tool.Result is not null)
        {
            InsertRowCore(
                working,
                "tool_call_result_size",
                [
                    ("span_id", record.RecordId),
                    ("utf8_bytes", tool.Result.Utf8Bytes),
                    ("characters", tool.Result.Characters),
                ]);

            if (tool.Result.PlainText is not null)
            {
                InsertRowCore(
                    working,
                    "tool_call_result_text",
                    [("span_id", record.RecordId), ("text", tool.Result.PlainText)]);
            }

            if (tool.Result.OriginalUtf8BytesWhenTruncated.HasValue)
            {
                InsertRowCore(
                    working,
                    "tool_call_result_truncation",
                    [("span_id", record.RecordId), ("original_utf8_bytes", tool.Result.OriginalUtf8BytesWhenTruncated.Value)]);
            }
        }

        if (tool.Succeeded.HasValue)
        {
            InsertRowCore(
                working,
                "tool_call_outcome",
                [("span_id", record.RecordId), ("succeeded", tool.Succeeded.Value ? 1L : 0L)]);
        }

        if (tool.Error is not null)
        {
            InsertRowCore(
                working,
                "tool_call_error",
                [("span_id", record.RecordId), ("message", tool.Error.Message)]);

            if (tool.Error.Code is not null)
            {
                InsertRowCore(
                    working,
                    "tool_call_error_code",
                    [("span_id", record.RecordId), ("code", tool.Error.Code)]);
            }
        }

        if (tool.ExitCode.HasValue)
        {
            InsertRowCore(
                working,
                "tool_call_exit_code",
                [("span_id", record.RecordId), ("exit_code", tool.ExitCode.Value)]);
        }
    }

    private static void InsertRecordAgentInvocation(StoreState working, CollectedRecord record)
    {
        if (record.Agent is null)
        {
            return;
        }

        CollectedAgentInvocation agent = record.Agent;
        InsertRowCore(
            working,
            "agent_invocation",
            [("span_id", record.RecordId), ("agent_name", agent.AgentName)]);

        if (agent.SpawningCallId is not null)
        {
            InsertRowCore(
                working,
                "agent_invocation_spawn",
                [("span_id", record.RecordId), ("spawning_record_id", agent.SpawningCallId)]);
        }

        if (agent.Succeeded.HasValue)
        {
            InsertRowCore(
                working,
                "agent_invocation_outcome",
                [("span_id", record.RecordId), ("succeeded", agent.Succeeded.Value ? 1L : 0L)]);
        }

        if (agent.Error is not null)
        {
            InsertRowCore(
                working,
                "agent_invocation_error",
                [("span_id", record.RecordId), ("message", agent.Error)]);
        }
    }

    private static void UpsertProblemCore(StoreState working, CollectedProblem problem)
    {
        TableMetadata meta = GetTableMetadata("ingest_problem");
        object?[]? existing = problem.ProblemKey is null
            ? null
            : FindRowByKey(working, "ingest_problem", [("problem_key", problem.ProblemKey)]);

        long problemId = existing is not null
            ? GetInt64(meta, existing, "problem_id")
            : working.NextProblemId++;

        if (existing is not null)
        {
            _ = DeleteRowsCore(working, "ingest_problem", [("problem_id", problemId)]);
        }

        InsertRowCore(
            working,
            "ingest_problem",
            [
                ("problem_id", problemId),
                ("problem_key", problem.ProblemKey),
                ("severity", problem.Severity),
                ("code", problem.Code),
                ("message", problem.Message),
            ]);

        if (problem.SpanId is not null)
        {
            InsertRowCore(working, "ingest_problem_span", [("problem_id", problemId), ("span_id", problem.SpanId)]);
        }

        if (problem.Location is not null)
        {
            InsertRowCore(working, "ingest_problem_location", [("problem_id", problemId), ("location", problem.Location)]);
        }

        if (problem.SessionKey is not null)
        {
            InsertRowCore(working, "ingest_problem_session", [("problem_id", problemId), ("session_id", problem.SessionKey)]);
        }

        if (problem.HarnessId is not null)
        {
            InsertRowCore(working, "ingest_problem_harness", [("problem_id", problemId), ("harness_id", problem.HarnessId)]);
        }

        if (problem.TimestampUtc is not null)
        {
            InsertRowCore(working, "ingest_problem_timestamp", [("problem_id", problemId), ("timestamp_utc", problem.TimestampUtc)]);
        }

        if (problem.Stack is not null)
        {
            InsertRowCore(working, "ingest_problem_stack", [("problem_id", problemId), ("stack", problem.Stack)]);
        }
    }

    private static void ReplaceSessionProjectionCore(StoreState working, StoredSessionProjection projection)
    {
        _ = DeleteRowsCore(working, "session_projection", [("session_id", projection.SessionId)]);

        InsertRowCore(
            working,
            "session_projection",
            [
                ("session_id", projection.SessionId),
                ("label", projection.Label),
                ("is_subagent", projection.IsSubagent ? 1L : 0L),
                ("span_count", (long)projection.SpanCount),
                ("started_at_utc", projection.StartedAtUtc),
                ("ended_at_utc", projection.EndedAtUtc),
            ]);

        if (projection.ParentSessionId is not null)
        {
            InsertRowCore(
                working,
                "session_projection_parent",
                [("session_id", projection.SessionId), ("parent_session_id", projection.ParentSessionId)]);
        }

        if (projection.AgentName is not null)
        {
            InsertRowCore(
                working,
                "session_projection_agent",
                [("session_id", projection.SessionId), ("agent_name", projection.AgentName)]);
        }

        if (projection.IncludedInParent)
        {
            InsertRowCore(working, "session_projection_included_in_parent", [("session_id", projection.SessionId)]);
        }

        if (projection.RequestSpanIds is not null)
        {
            for (int index = 0; index < projection.RequestSpanIds.Count; index++)
            {
                InsertRowCore(
                    working,
                    "session_request",
                    [
                        ("session_id", projection.SessionId),
                        ("position", (long)index),
                        ("request_span_id", projection.RequestSpanIds[index]),
                    ]);
            }
        }

        if (projection.Turns is not null)
        {
            foreach (StoredSessionTurn turn in projection.Turns)
            {
                ArgumentNullException.ThrowIfNull(turn);
                InsertRowCore(
                    working,
                    "session_turn",
                    [
                        ("session_id", turn.SessionId),
                        ("position", (long)turn.Position),
                        ("span_id", turn.SpanId),
                    ]);
            }
        }

        if (projection.Context is not null)
        {
            InsertSessionContextCore(working, projection.SessionId, projection.Context);
        }

        if (projection.ContextUnmeasurableReason is not null)
        {
            InsertRowCore(
                working,
                "session_context_unmeasurable",
                [("session_id", projection.SessionId), ("reason", projection.ContextUnmeasurableReason)]);
        }

        if (projection.Schema is not null)
        {
            InsertSessionSchemaCore(working, projection.SessionId, projection.Schema);
        }

        if (projection.SchemaUnmeasurableReason is not null)
        {
            InsertRowCore(
                working,
                "session_schema_unmeasurable",
                [("session_id", projection.SessionId), ("reason", projection.SchemaUnmeasurableReason)]);
        }

        if (projection.TimelineSubagentSpanIds is not null)
        {
            foreach (string spanId in projection.TimelineSubagentSpanIds)
            {
                InsertRowCore(
                    working,
                    "session_timeline_subagent",
                    [("session_id", projection.SessionId), ("span_id", spanId)]);
            }
        }

        if (projection.TimelineAuxiliarySpanIds is not null)
        {
            foreach (string spanId in projection.TimelineAuxiliarySpanIds)
            {
                InsertRowCore(
                    working,
                    "session_timeline_auxiliary",
                    [("session_id", projection.SessionId), ("span_id", spanId)]);
            }
        }
    }

    private static void InsertSessionContextCore(StoreState working, string sessionId, StoredSessionContext context)
    {
        InsertRowCore(
            working,
            "session_context",
            [
                ("session_id", sessionId),
                ("context_limit", context.ContextLimit),
                ("context_limit_source", context.ContextLimitSource),
                ("residual_total", context.ResidualTotal),
                ("fresh_jump_factor", context.FreshJumpFactor),
                ("accumulation_rate", context.AccumulationRate),
            ]);

        if (context.ObservedBuckets is not null)
        {
            foreach (string bucket in context.ObservedBuckets)
            {
                InsertRowCore(
                    working,
                    "session_context_observed_part",
                    [("session_id", sessionId), ("bucket", bucket)]);
            }
        }

        if (context.DerivedCounts is not null)
        {
            foreach (StoredSessionContextDerivedCount counter in context.DerivedCounts)
            {
                ArgumentNullException.ThrowIfNull(counter);
                InsertRowCore(
                    working,
                    "session_context_derived_count",
                    [("session_id", sessionId), ("counter", counter.Counter), ("count", counter.Count)]);
            }
        }

        if (context.FlaggedTurnIndexes is not null)
        {
            foreach (int turnIndex in context.FlaggedTurnIndexes)
            {
                InsertRowCore(
                    working,
                    "session_context_flagged_turn",
                    [("session_id", sessionId), ("turn_index", (long)turnIndex)]);
            }
        }

        if (context.Turns is null)
        {
            return;
        }

        foreach (StoredSessionContextTurn turn in context.Turns)
        {
            ArgumentNullException.ThrowIfNull(turn);
            InsertRowCore(
                working,
                "session_context_turn",
                [
                    ("session_id", sessionId),
                    ("position", (long)turn.Position),
                    ("input_tokens", turn.InputTokens),
                    ("bucketed_tokens", turn.BucketedTokens),
                    ("residual_tokens", turn.ResidualTokens),
                    ("residual_attribution", turn.ResidualAttribution),
                    ("builtin_tool_definition_tokens", turn.BuiltinToolDefinitionTokens),
                    ("fresh_input", turn.FreshInput),
                ]);

            if (turn.Headroom.HasValue || turn.Pressure.HasValue)
            {
                InsertRowCore(
                    working,
                    "session_context_turn_headroom",
                    [
                        ("session_id", sessionId),
                        ("position", (long)turn.Position),
                        ("headroom", turn.Headroom),
                        ("pressure", turn.Pressure),
                    ]);
            }

            if (turn.AccumulationRate.HasValue)
            {
                InsertRowCore(
                    working,
                    "session_context_turn_accumulation",
                    [
                        ("session_id", sessionId),
                        ("position", (long)turn.Position),
                        ("rate", turn.AccumulationRate.Value),
                    ]);
            }

            if (turn.Buckets is not null)
            {
                foreach (StoredTurnBucketTokens bucket in turn.Buckets)
                {
                    ArgumentNullException.ThrowIfNull(bucket);
                    InsertRowCore(
                        working,
                        "session_context_turn_bucket",
                        [
                            ("session_id", sessionId),
                            ("position", (long)turn.Position),
                            ("bucket", bucket.Bucket),
                            ("tokens", bucket.Tokens),
                        ]);
                }
            }

            if (turn.ServerDefinitions is not null)
            {
                foreach (StoredTurnServerDefinitionTokens serverDef in turn.ServerDefinitions)
                {
                    ArgumentNullException.ThrowIfNull(serverDef);
                    InsertRowCore(
                        working,
                        "session_context_turn_server_definitions",
                        [
                            ("session_id", sessionId),
                            ("position", (long)turn.Position),
                            ("server", serverDef.Server),
                            ("tokens", serverDef.Tokens),
                        ]);
                }
            }

            if (turn.StrippedBlockCount.HasValue || turn.StrippedTokens.HasValue)
            {
                InsertRowCore(
                    working,
                    "session_context_turn_stripped_instructions",
                    [
                        ("session_id", sessionId),
                        ("position", (long)turn.Position),
                        ("block_count", turn.StrippedBlockCount),
                        ("tokens", turn.StrippedTokens),
                    ]);
            }

            if (turn.PreviousFreshInput.HasValue || turn.FreshJumpFactor.HasValue)
            {
                InsertRowCore(
                    working,
                    "session_context_turn_fresh_jump",
                    [
                        ("session_id", sessionId),
                        ("position", (long)turn.Position),
                        ("previous_fresh_input", turn.PreviousFreshInput),
                        ("factor", turn.FreshJumpFactor),
                    ]);
            }

            if (turn.NonResidentTokens.HasValue)
            {
                InsertRowCore(
                    working,
                    "session_context_turn_non_resident",
                    [
                        ("session_id", sessionId),
                        ("position", (long)turn.Position),
                        ("tokens", turn.NonResidentTokens.Value),
                    ]);
            }
        }
    }

    private static void InsertSessionSchemaCore(StoreState working, string sessionId, StoredSessionSchema schema)
    {
        InsertRowCore(
            working,
            "session_schema",
            [
                ("session_id", sessionId),
                ("invocation_count", (long)schema.InvocationCount),
                ("turn_count", (long)schema.TurnCount),
            ]);

        if (schema.RankedTools is not null)
        {
            foreach (StoredSchemaRankedTool ranked in schema.RankedTools)
            {
                ArgumentNullException.ThrowIfNull(ranked);
                InsertRowCore(
                    working,
                    "session_schema_ranked_tool",
                    [
                        ("session_id", sessionId),
                        ("position", (long)ranked.Position),
                        ("tool_name", ranked.ToolName),
                        ("cost_tokens", ranked.CostTokens),
                        ("invoked", ranked.Invoked ? 1L : 0L),
                    ]);

                if (ranked.ServerName is not null)
                {
                    InsertRowCore(
                        working,
                        "session_schema_ranked_tool_server",
                        [
                            ("session_id", sessionId),
                            ("position", (long)ranked.Position),
                            ("server_name", ranked.ServerName),
                        ]);
                }
            }
        }

        if (schema.Servers is not null)
        {
            foreach (StoredSchemaServerTokens server in schema.Servers)
            {
                ArgumentNullException.ThrowIfNull(server);
                InsertRowCore(
                    working,
                    "session_schema_server",
                    [
                        ("session_id", sessionId),
                        ("server_name", server.ServerName),
                        ("tokens", server.Tokens),
                    ]);
            }
        }

        if (schema.UnusedTokenResidencies.HasValue)
        {
            InsertRowCore(
                working,
                "session_schema_unused",
                [("session_id", sessionId), ("token_residencies", schema.UnusedTokenResidencies.Value)]);
        }
    }

    private static void UpsertRunCore(StoreState working, StoredRun run)
    {
        TableMetadata meta = GetTableMetadata("run");
        object?[]? existing = FindRowByKey(working, "run", [("run_id", run.RunId)]);

        if (existing is null)
        {
            InsertRowCore(
                working,
                "run",
                [
                    ("run_id", run.RunId),
                    ("harness_id", run.HarnessId),
                    ("grouping_basis", run.GroupingBasis),
                    ("grouping_rule", run.GroupingRule),
                    ("execution_count", (long)run.ExecutionCount),
                ]);
        }
        else
        {
            object?[] replacement = ValidateAndNormalizeRow(
                working,
                meta,
                [
                    ("run_id", run.RunId),
                    ("harness_id", run.HarnessId),
                    ("grouping_basis", run.GroupingBasis),
                    ("grouping_rule", run.GroupingRule),
                    ("execution_count", (long)run.ExecutionCount),
                ],
                skipPkDuplicateCheck: true,
                existingRowForUniqueCheck: existing);
            Array.Copy(replacement, existing, replacement.Length);

            _ = DeleteRowsCore(working, "run_label", [("run_id", run.RunId)]);
            _ = DeleteRowsCore(working, "run_working_directory", [("run_id", run.RunId)]);
            _ = DeleteRowsCore(working, "run_bounds", [("run_id", run.RunId)]);
        }

        if (run.Label is not null)
        {
            InsertRowCore(working, "run_label", [("run_id", run.RunId), ("label", run.Label)]);
        }

        if (run.WorkingDirectory is not null)
        {
            InsertRowCore(working, "run_working_directory", [("run_id", run.RunId), ("working_directory", run.WorkingDirectory)]);
        }

        if (run.StartedAtUtc is not null || run.EndedAtUtc is not null)
        {
            InsertRowCore(
                working,
                "run_bounds",
                [
                    ("run_id", run.RunId),
                    ("started_at_utc", run.StartedAtUtc),
                    ("ended_at_utc", run.EndedAtUtc),
                ]);
        }
    }

    private static Dictionary<string, object?> BuildValueLookup((string ColumnName, object? Value)[] values)
    {
        Dictionary<string, object?> map = new(values.Length, StringComparer.Ordinal);
        foreach ((string columnName, object? value) in values)
        {
            if (string.IsNullOrWhiteSpace(columnName) || !map.TryAdd(columnName, value))
            {
                throw new DashValidationException(
                    ConstraintViolatedCode,
                    $"Invalid or duplicate column specification '{columnName}'.");
            }
        }

        return map;
    }

    private static void InsertRowCore(StoreState working, string tableName, (string ColumnName, object? Value)[] values) =>
        InsertRowCore(working, tableName, BuildValueLookup(values));

    private static void InsertRowCore(StoreState working, string tableName, Dictionary<string, object?> values)
    {
        TableMetadata meta = GetTableMetadata(tableName);
        object?[] row = ValidateAndNormalizeRow(working, meta, values, skipPkDuplicateCheck: false, existingRowForUniqueCheck: null);
        working.GetRows(meta.Definition.Name).Add(row);
    }

    private static object?[] ValidateAndNormalizeRow(
        StoreState working,
        TableMetadata meta,
        (string ColumnName, object? Value)[] values,
        bool skipPkDuplicateCheck,
        object?[]? existingRowForUniqueCheck) =>
        ValidateAndNormalizeRow(working, meta, BuildValueLookup(values), skipPkDuplicateCheck, existingRowForUniqueCheck);

    private static object?[] ValidateAndNormalizeRow(
        StoreState working,
        TableMetadata meta,
        Dictionary<string, object?> values,
        bool skipPkDuplicateCheck,
        object?[]? existingRowForUniqueCheck)
    {
        TableDefinition table = meta.Definition;
        foreach (string suppliedColumn in values.Keys)
        {
            if (!meta.ColumnIndices.ContainsKey(suppliedColumn))
            {
                throw new DashValidationException(
                    ConstraintViolatedCode,
                    $"Unknown column '{suppliedColumn}' on table '{table.Name}'.");
            }
        }

        object?[] row = new object?[table.Columns.Count];
        for (int index = 0; index < table.Columns.Count; index++)
        {
            ColumnDefinition col = table.Columns[index];
            bool present = values.TryGetValue(col.Name, out object? rawValue);

            if (!present || rawValue is null)
            {
                if (col.IsNotNull)
                {
                    throw new DashValidationException(
                        ConstraintViolatedCode,
                        $"NOT NULL constraint violated on '{table.Name}.{col.Name}'.");
                }

                row[index] = null;
                continue;
            }

            object normalized = NormalizeAndValidateValue(table.Name, col, rawValue);
            row[index] = normalized;
        }

        for (int index = 0; index < table.Columns.Count; index++)
        {
            ColumnDefinition col = table.Columns[index];
            if (col.CheckConstraint is not null && row[index] is not null)
            {
                EvaluateCheckConstraint(table.Name, col.Name, col.CheckConstraint, row[index]!);
            }
        }

        List<object?[]> existingRows = working.GetRows(table.Name);

        if (!skipPkDuplicateCheck)
        {
            foreach (object?[] existing in existingRows)
            {
                if (KeysMatch(meta.PrimaryKeyIndices, existing, meta.PrimaryKeyIndices, row))
                {
                    throw new DashValidationException(
                        ConstraintViolatedCode,
                        $"Primary key constraint violated on '{table.Name}'.");
                }
            }
        }

        foreach (IndexMetadata uniqueIndex in meta.UniqueIndexes)
        {
            foreach (object?[] existing in existingRows)
            {
                if (ReferenceEquals(existing, existingRowForUniqueCheck))
                {
                    continue;
                }

                if (KeysMatch(uniqueIndex.ColumnIndices, existing, uniqueIndex.ColumnIndices, row))
                {
                    throw new DashValidationException(
                        ConstraintViolatedCode,
                        $"Unique constraint '{uniqueIndex.Name}' violated on '{table.Name}'.");
                }
            }
        }

        foreach (ForeignKeyMetadata fk in meta.ForeignKeys)
        {
            List<object?[]> parentRows = working.GetRows(fk.ReferencedTable);
            bool parentFound = false;
            foreach (object?[] parentRow in parentRows)
            {
                if (KeysMatch(fk.ReferencedIndices, parentRow, fk.LocalIndices, row))
                {
                    parentFound = true;
                    break;
                }
            }

            if (!parentFound)
            {
                throw new DashValidationException(
                    ConstraintViolatedCode,
                    $"Foreign key constraint violated on '{table.Name}' -> '{fk.ReferencedTable}'.");
            }
        }

        ValidateExclusivePairs(working, meta, row);
        return row;
    }

    private static object NormalizeAndValidateValue(string tableName, ColumnDefinition col, object rawValue)
    {
        switch (col.Type)
        {
            case SqliteColumnType.SqliteText:
                if (rawValue is not string text)
                {
                    throw new DashValidationException(
                        ConstraintViolatedCode,
                        $"Expected TEXT value for '{tableName}.{col.Name}'.");
                }

                if (JsonDocumentText.IsDocument(text))
                {
                    throw new DashValidationException(
                        JsonTextRefusedCode,
                        $"Refused JSON object or array text in '{tableName}.{col.Name}'.");
                }

                return text;

            case SqliteColumnType.SqliteInteger:
                return rawValue switch
                {
                    long l => l,
                    int i => (long)i,
                    short s => (long)s,
                    byte b => (long)b,
                    bool flag => flag ? 1L : 0L,
                    _ => throw new DashValidationException(
                        ConstraintViolatedCode,
                        $"Expected INTEGER value for '{tableName}.{col.Name}'."),
                };

            case SqliteColumnType.SqliteReal:
                double number = rawValue switch
                {
                    double d => d,
                    float f => f,
                    decimal m => (double)m,
                    long l => l,
                    int i => i,
                    _ => throw new DashValidationException(
                        ConstraintViolatedCode,
                        $"Expected REAL value for '{tableName}.{col.Name}'."),
                };

                if (double.IsNaN(number) || double.IsInfinity(number))
                {
                    throw new DashValidationException(
                        ConstraintViolatedCode,
                        $"REAL value for '{tableName}.{col.Name}' must be finite.");
                }

                return number;

            default:
                throw new DashValidationException(
                    ConstraintViolatedCode,
                    $"Unsupported column type '{col.Type}' on '{tableName}.{col.Name}'.");
        }
    }

    private static void EvaluateCheckConstraint(string tableName, string columnName, string checkConstraint, object value)
    {
        string[] clauses = checkConstraint.Split(" AND ", StringSplitOptions.TrimEntries);
        foreach (string clause in clauses)
        {
            if (!EvaluateSingleCheckClause(columnName, clause, value))
            {
                throw new DashValidationException(
                    ConstraintViolatedCode,
                    $"CHECK constraint '{checkConstraint}' violated on '{tableName}.{columnName}'.");
            }
        }
    }

    private static bool EvaluateSingleCheckClause(string columnName, string clause, object value)
    {
        string trimmed = clause.Trim();

        string lengthGreaterZero = string.Create(CultureInfo.InvariantCulture, $"length({columnName}) > 0");
        if (string.Equals(trimmed, lengthGreaterZero, StringComparison.Ordinal))
        {
            return value is string str && str.Length > 0;
        }

        string lengthPrefix = string.Create(CultureInfo.InvariantCulture, $"length({columnName}) = ");
        if (trimmed.StartsWith(lengthPrefix, StringComparison.Ordinal))
        {
            int expectedLength = int.Parse(trimmed[lengthPrefix.Length..], CultureInfo.InvariantCulture);
            return value is string str && str.Length == expectedLength;
        }

        string inPrefix = string.Create(CultureInfo.InvariantCulture, $"{columnName} IN (");
        if (trimmed.StartsWith(inPrefix, StringComparison.Ordinal) && trimmed.EndsWith(')'))
        {
            string body = trimmed[inPrefix.Length..^1];
            string[] tokens = body.Split(',', StringSplitOptions.TrimEntries);
            foreach (string token in tokens)
            {
                if (token.StartsWith('\'') && token.EndsWith('\'') && token.Length >= 2)
                {
                    string literal = token[1..^1];
                    if (value is string str && string.Equals(str, literal, StringComparison.Ordinal))
                    {
                        return true;
                    }
                }
                else if (long.TryParse(token, CultureInfo.InvariantCulture, out long allowedLong) &&
                         value is long actualLong &&
                         actualLong == allowedLong)
                {
                    return true;
                }
            }

            return false;
        }

        if (TryMatchComparison(columnName, trimmed, " >= ", out string? geLiteral))
        {
            return CompareNumeric(value, geLiteral) >= 0;
        }

        if (TryMatchComparison(columnName, trimmed, " <= ", out string? leLiteral))
        {
            return CompareNumeric(value, leLiteral) <= 0;
        }

        if (TryMatchComparison(columnName, trimmed, " > ", out string? gtLiteral))
        {
            return CompareNumeric(value, gtLiteral) > 0;
        }

        if (TryMatchComparison(columnName, trimmed, " = ", out string? eqLiteral))
        {
            return CompareNumeric(value, eqLiteral) == 0;
        }

        throw new InvalidOperationException($"Unsupported catalog CHECK constraint clause '{clause}'.");
    }

    private static bool TryMatchComparison(string columnName, string clause, string op, out string literal)
    {
        string prefix = columnName + op;
        if (clause.StartsWith(prefix, StringComparison.Ordinal))
        {
            literal = clause[prefix.Length..].Trim();
            return true;
        }

        literal = string.Empty;
        return false;
    }

    private static int CompareNumeric(object value, string literal)
    {
        if (value is long l && long.TryParse(literal, CultureInfo.InvariantCulture, out long rhsLong))
        {
            return l.CompareTo(rhsLong);
        }

        double lhs = Convert.ToDouble(value, CultureInfo.InvariantCulture);
        double rhs = double.Parse(literal, CultureInfo.InvariantCulture);
        return lhs.CompareTo(rhs);
    }

    private static void ValidateExclusivePairs(StoreState working, TableMetadata targetMeta, object?[] targetRow)
    {
        foreach ((string firstTable, string secondTable, string[] keyColumns) in ExclusivePairs)
        {
            string? counterpartTable = string.Equals(targetMeta.Definition.Name, firstTable, StringComparison.Ordinal)
                ? secondTable
                : string.Equals(targetMeta.Definition.Name, secondTable, StringComparison.Ordinal)
                    ? firstTable
                    : null;

            if (counterpartTable is null)
            {
                continue;
            }

            TableMetadata counterpartMeta = GetTableMetadata(counterpartTable);
            int[] targetKeyIndices = ResolveIndices(targetMeta, keyColumns);
            int[] counterpartKeyIndices = ResolveIndices(counterpartMeta, keyColumns);

            foreach (object?[] existing in working.GetRows(counterpartTable))
            {
                if (KeysMatch(targetKeyIndices, targetRow, counterpartKeyIndices, existing))
                {
                    throw new DashValidationException(
                        ConstraintViolatedCode,
                        $"Exclusive-pair constraint violated between '{firstTable}' and '{secondTable}'.");
                }
            }
        }
    }

    private static int DeleteRowsCore(
        StoreState working,
        string tableName,
        (string ColumnName, object? Value)[] predicate)
    {
        TableMetadata rootMeta = GetTableMetadata(tableName);
        foreach ((string colName, _) in predicate)
        {
            if (!rootMeta.ColumnIndices.ContainsKey(colName))
            {
                throw new DashValidationException(
                    ConstraintViolatedCode,
                    $"Unknown column '{colName}' in delete predicate for '{tableName}'.");
            }
        }

        List<object?[]> tableRows = working.GetRows(rootMeta.Definition.Name);
        Queue<(string TableName, object?[] DeletedRow)> cascadeQueue = new();
        int primaryDeleted = 0;

        for (int index = tableRows.Count - 1; index >= 0; index--)
        {
            object?[] candidate = tableRows[index];
            if (MatchesPredicate(rootMeta, candidate, predicate))
            {
                tableRows.RemoveAt(index);
                cascadeQueue.Enqueue((rootMeta.Definition.Name, candidate));
                primaryDeleted++;
            }
        }

        while (cascadeQueue.Count > 0)
        {
            (string parentTable, object?[] deletedParentRow) = cascadeQueue.Dequeue();
            TableMetadata parentMeta = GetTableMetadata(parentTable);

            foreach (ForeignKeyReference dependent in parentMeta.DependentForeignKeys)
            {
                List<object?[]> childRows = working.GetRows(dependent.ChildMeta.Definition.Name);
                for (int index = childRows.Count - 1; index >= 0; index--)
                {
                    object?[] childRow = childRows[index];
                    if (KeysMatch(dependent.ForeignKey.ReferencedIndices, deletedParentRow, dependent.ForeignKey.LocalIndices, childRow))
                    {
                        childRows.RemoveAt(index);
                        cascadeQueue.Enqueue((dependent.ChildMeta.Definition.Name, childRow));
                    }
                }
            }
        }

        return primaryDeleted;
    }

    private static bool MatchesPredicate(
        TableMetadata meta,
        object?[] row,
        (string ColumnName, object? Value)[] predicate)
    {
        foreach ((string colName, object? expectedRaw) in predicate)
        {
            int colIdx = meta.ColumnIndices[colName];
            object? expected = NormalizePredicateValue(meta.Definition.Columns[colIdx].Type, expectedRaw);
            if (!ValuesEqual(row[colIdx], expected))
            {
                return false;
            }
        }

        return true;
    }

    private static object? NormalizePredicateValue(SqliteColumnType type, object? raw) =>
        raw switch
        {
            null => null,
            int i when type == SqliteColumnType.SqliteInteger => (long)i,
            short s when type == SqliteColumnType.SqliteInteger => (long)s,
            byte b when type == SqliteColumnType.SqliteInteger => (long)b,
            bool flag when type == SqliteColumnType.SqliteInteger => flag ? 1L : 0L,
            float f when type == SqliteColumnType.SqliteReal => (double)f,
            decimal m when type == SqliteColumnType.SqliteReal => (double)m,
            long l when type == SqliteColumnType.SqliteReal => (double)l,
            int i when type == SqliteColumnType.SqliteReal => (double)i,
            _ => raw,
        };

    private static object?[]? FindRowByKey(
        StoreState currentState,
        string tableName,
        (string ColumnName, object? Value)[] predicate)
    {
        TableMetadata meta = GetTableMetadata(tableName);
        foreach (object?[] row in currentState.GetRows(meta.Definition.Name))
        {
            if (MatchesPredicate(meta, row, predicate))
            {
                return row;
            }
        }

        return null;
    }

    private static bool KeysMatch(int[] leftIndices, object?[] leftRow, int[] rightIndices, object?[] rightRow)
    {
        for (int index = 0; index < leftIndices.Length; index++)
        {
            if (!ValuesEqual(leftRow[leftIndices[index]], rightRow[rightIndices[index]]))
            {
                return false;
            }
        }

        return true;
    }

    private static bool ValuesEqual(object? left, object? right)
    {
        if (left is null || right is null)
        {
            return left is null && right is null;
        }

        if (left is string ls && right is string rs)
        {
            return string.Equals(ls, rs, StringComparison.Ordinal);
        }

        if (left is long ll && right is long rl)
        {
            return ll == rl;
        }

        if (left is double ld && right is double rd)
        {
            return ld.Equals(rd);
        }

        return Equals(left, right);
    }

    private static CollectedSession MaterializeSession(StoreState currentState, object?[] sessionRow)
    {
        TableMetadata sessionMeta = GetTableMetadata("session");
        string sessionId = GetString(sessionMeta, sessionRow, "session_id");
        string harnessId = GetString(sessionMeta, sessionRow, "harness_id");
        string nativeSessionId = GetString(sessionMeta, sessionRow, "native_session_id");

        object?[]? wsRow = FindRowByKey(currentState, "session_workspace", [("session_id", sessionId)]);
        object?[]? repoRow = FindRowByKey(currentState, "session_repository", [("session_id", sessionId)]);
        object?[]? branchRow = FindRowByKey(currentState, "session_branch", [("session_id", sessionId)]);

        CollectedWorkspace? workspace = wsRow is null && repoRow is null && branchRow is null
            ? null
            : new CollectedWorkspace(
                wsRow is null ? null : GetString(GetTableMetadata("session_workspace"), wsRow, "working_directory"),
                repoRow is null ? null : GetString(GetTableMetadata("session_repository"), repoRow, "repo_name"),
                branchRow is null ? null : GetString(GetTableMetadata("session_branch"), branchRow, "branch_name"));

        object?[]? titleRow = FindRowByKey(currentState, "session_title", [("session_id", sessionId)]);
        string? title = titleRow is null ? null : GetString(GetTableMetadata("session_title"), titleRow, "title");

        TableMetadata usageMeta = GetTableMetadata("session_model_usage");
        List<CollectedModelUsage> usages = [];
        foreach (object?[] usageRow in currentState.GetRows("session_model_usage")
            .Where(r => string.Equals(GetString(usageMeta, r, "session_id"), sessionId, StringComparison.Ordinal))
            .OrderBy(r => (int)GetInt64(usageMeta, r, "snapshot_position"))
            .ThenBy(r => GetString(usageMeta, r, "model_id"), StringComparer.Ordinal)
            .ThenBy(r => GetString(usageMeta, r, "token_class"), StringComparer.Ordinal))
        {
            usages.Add(new CollectedModelUsage(
                (int)GetInt64(usageMeta, usageRow, "snapshot_position"),
                GetString(usageMeta, usageRow, "model_id"),
                ParseTokenClass(GetString(usageMeta, usageRow, "token_class")),
                GetInt64(usageMeta, usageRow, "token_count")));
        }

        return new CollectedSession(sessionId, harnessId, nativeSessionId, workspace, title, usages);
    }

    private static CollectedRecord MaterializeRecord(StoreState currentState, object?[] recordRow)
    {
        TableMetadata meta = GetTableMetadata("record");
        string spanId = GetString(meta, recordRow, "span_id");
        string harnessId = GetString(meta, recordRow, "harness_id");
        RecordOp op = ParseRecordOp(GetString(meta, recordRow, "op"));
        RecordKind kind = ParseRecordKind(GetString(meta, recordRow, "kind"));
        string name = GetString(meta, recordRow, "name");
        DateTimeOffset startedAt = ParseTimestamp(GetString(meta, recordRow, "started_at_utc"));
        RecordStatus status = ParseRecordStatus(GetString(meta, recordRow, "status"));

        string? sessionKey = ReadOptionalSingleString(currentState, "record_session_link", "span_id", spanId, "session_key");
        string? parentSpanId = ReadOptionalSingleString(currentState, "record_parent", "span_id", spanId, "parent_span_id");
        string? traceId = ReadOptionalSingleString(currentState, "record_trace", "span_id", spanId, "trace_id");
        string? model = ReadOptionalSingleString(currentState, "record_model", "span_id", spanId, "model_id");
        string? endedRaw = ReadOptionalSingleString(currentState, "record_end", "span_id", spanId, "ended_at_utc");
        DateTimeOffset? endedAt = endedRaw is null ? null : ParseTimestamp(endedRaw);
        long? reportedDurationMs = ReadOptionalSingleInt64(currentState, "record_reported_duration", "span_id", spanId, "duration_ms");
        string? agentInstanceId = ReadOptionalSingleString(currentState, "record_agent_instance", "span_id", spanId, "agent_instance_id");

        TableMetadata tokenMeta = GetTableMetadata("record_token_count");
        List<CollectedTokenCount> tokens =
        [
            .. currentState.GetRows("record_token_count")
                .Where(r => string.Equals(GetString(tokenMeta, r, "span_id"), spanId, StringComparison.Ordinal))
                .Select(r => new CollectedTokenCount(
                    ParseTokenClass(GetString(tokenMeta, r, "token_class")),
                    GetInt64(tokenMeta, r, "token_count"))),
        ];

        object?[]? costRow = FindRowByKey(currentState, "record_reported_cost", [("span_id", spanId)]);
        CollectedCost? reportedCost = costRow is null
            ? null
            : new CollectedCost(
                GetDouble(GetTableMetadata("record_reported_cost"), costRow, "amount"),
                GetString(GetTableMetadata("record_reported_cost"), costRow, "currency"));

        TableMetadata skillMeta = GetTableMetadata("record_skill_invocation");
        List<string> skills =
        [
            .. currentState.GetRows("record_skill_invocation")
                .Where(r => string.Equals(GetString(skillMeta, r, "span_id"), spanId, StringComparison.Ordinal))
                .Select(r => GetString(skillMeta, r, "skill_name")),
        ];

        TableMetadata partMeta = GetTableMetadata("content_part");
        List<CollectedContentPart> parts =
        [
            .. currentState.GetRows("content_part")
                .Where(r => string.Equals(GetString(partMeta, r, "span_id"), spanId, StringComparison.Ordinal))
                .OrderBy(r => (int)GetInt64(partMeta, r, "position"))
                .Select(r => MaterializeContentPart(currentState, r)),
        ];

        TableMetadata defMeta = GetTableMetadata("tool_definition");
        List<CollectedToolDefinition> toolDefs =
        [
            .. currentState.GetRows("tool_definition")
                .Where(r => string.Equals(GetString(defMeta, r, "span_id"), spanId, StringComparison.Ordinal))
                .OrderBy(r => (int)GetInt64(defMeta, r, "position"))
                .Select(r => MaterializeToolDefinition(currentState, r)),
        ];

        CollectedToolCall? toolCall = MaterializeToolCall(currentState, spanId);
        CollectedAgentInvocation? agentInvocation = MaterializeAgentInvocation(currentState, spanId);
        CollectedRecordSignals signals = MaterializeRecordSignals(currentState, spanId);

        long? reportedInput = ReadOptionalReportedTotal(currentState, spanId, "input");
        long? reportedOutput = ReadOptionalReportedTotal(currentState, spanId, "output");

        return new CollectedRecord(
            spanId,
            harnessId,
            op,
            kind,
            name,
            startedAt,
            status,
            sessionKey,
            parentSpanId,
            traceId,
            model,
            endedAt,
            reportedDurationMs,
            tokens,
            reportedCost,
            parts,
            toolCall,
            agentInvocation,
            skills,
            agentInstanceId,
            toolDefs,
            signals,
            reportedInput,
            reportedOutput);
    }

    private static long? ReadOptionalReportedTotal(StoreState currentState, string spanId, string direction)
    {
        object?[]? row = FindRowByKey(
            currentState,
            "record_reported_total",
            [("span_id", spanId), ("direction", direction)]);
        return row is null ? null : GetInt64(GetTableMetadata("record_reported_total"), row, "token_count");
    }

    private static CollectedContentPart MaterializeContentPart(StoreState currentState, object?[] partRow)
    {
        TableMetadata meta = GetTableMetadata("content_part");
        string spanId = GetString(meta, partRow, "span_id");
        long pos = GetInt64(meta, partRow, "position");
        ContentBucket bucket = ParseContentBucket(GetString(meta, partRow, "bucket"));

        object?[]? textRow = FindRowByKey(currentState, "content_part_text", [("span_id", spanId), ("position", pos)]);
        string? plainText = textRow is null ? null : GetString(GetTableMetadata("content_part_text"), textRow, "part_text");

        object?[]? withheldRow = FindRowByKey(currentState, "content_part_withheld", [("span_id", spanId), ("position", pos)]);
        CollectedWithheldText? withheld = null;
        if (withheldRow is not null)
        {
            TableMetadata withheldMeta = GetTableMetadata("content_part_withheld");
            withheld = new CollectedWithheldText(
                GetInt64(withheldMeta, withheldRow, "utf8_bytes"),
                GetInt64(withheldMeta, withheldRow, "characters"),
                GetInt64(withheldMeta, withheldRow, "measured_tokens"));
        }

        object?[]? repTokensRow = FindRowByKey(currentState, "content_part_reported_tokens", [("span_id", spanId), ("position", pos)]);
        long? reportedTokens = repTokensRow is null ? null : GetInt64(GetTableMetadata("content_part_reported_tokens"), repTokensRow, "token_count");

        object?[]? serverRow = FindRowByKey(currentState, "content_part_server", [("span_id", spanId), ("position", pos)]);
        string? server = serverRow is null ? null : GetString(GetTableMetadata("content_part_server"), serverRow, "server_name");

        object?[]? roleRow = FindRowByKey(currentState, "content_part_role", [("span_id", spanId), ("position", pos)]);
        string? role = roleRow is null ? null : GetString(GetTableMetadata("content_part_role"), roleRow, "role_name");

        object?[]? resRow = FindRowByKey(currentState, "content_part_residency", [("span_id", spanId), ("position", pos)]);
        bool? resident = resRow is null ? null : GetInt64(GetTableMetadata("content_part_residency"), resRow, "is_resident") == 1L;

        object?[]? truncRow = FindRowByKey(currentState, "content_part_truncation", [("span_id", spanId), ("position", pos)]);
        long? origBytes = truncRow is null ? null : GetInt64(GetTableMetadata("content_part_truncation"), truncRow, "original_utf8_bytes");

        return new CollectedContentPart(
            (int)pos,
            bucket,
            plainText,
            withheld,
            reportedTokens,
            server,
            role,
            resident,
            origBytes);
    }

    private static CollectedToolDefinition MaterializeToolDefinition(StoreState currentState, object?[] row)
    {
        TableMetadata meta = GetTableMetadata("tool_definition");
        string spanId = GetString(meta, row, "span_id");
        long pos = GetInt64(meta, row, "position");
        string toolName = GetString(meta, row, "tool_name");
        long schemaTokens = GetInt64(meta, row, "schema_tokens");

        object?[]? serverRow = FindRowByKey(currentState, "tool_definition_server", [("span_id", spanId), ("position", pos)]);
        string? server = serverRow is null ? null : GetString(GetTableMetadata("tool_definition_server"), serverRow, "server_name");

        return new CollectedToolDefinition((int)pos, toolName, server, schemaTokens);
    }

    private static CollectedToolCall? MaterializeToolCall(StoreState currentState, string spanId)
    {
        object?[]? row = FindRowByKey(currentState, "tool_call", [("span_id", spanId)]);
        if (row is null)
        {
            return null;
        }

        TableMetadata meta = GetTableMetadata("tool_call");
        string callId = GetString(meta, row, "call_id");
        string toolName = GetString(meta, row, "tool_name");

        object?[]? mcpRow = FindRowByKey(currentState, "tool_call_mcp", [("span_id", spanId)]);
        string? mcpServer = mcpRow is null ? null : GetString(GetTableMetadata("tool_call_mcp"), mcpRow, "server_name");
        string? mcpTool = mcpRow is null ? null : GetString(GetTableMetadata("tool_call_mcp"), mcpRow, "server_tool_name");

        CollectedToolContent? input = MaterializeToolContent(currentState, spanId, isInput: true);
        CollectedToolContent? result = MaterializeToolContent(currentState, spanId, isInput: false);

        long? outcomeInt = ReadOptionalSingleInt64(currentState, "tool_call_outcome", "span_id", spanId, "succeeded");
        bool? succeeded = outcomeInt.HasValue ? outcomeInt.Value == 1L : null;

        string? errorMsg = ReadOptionalSingleString(currentState, "tool_call_error", "span_id", spanId, "message");
        string? errorCode = ReadOptionalSingleString(currentState, "tool_call_error_code", "span_id", spanId, "code");
        CollectedError? error = errorMsg is null ? null : new CollectedError(errorMsg, errorCode);

        long? exitCode = ReadOptionalSingleInt64(currentState, "tool_call_exit_code", "span_id", spanId, "exit_code");

        return new CollectedToolCall(callId, toolName, mcpServer, mcpTool, input, result, succeeded, error, exitCode);
    }

    private static CollectedToolContent? MaterializeToolContent(StoreState currentState, string spanId, bool isInput)
    {
        string sizeTable = isInput ? "tool_call_input_size" : "tool_call_result_size";
        string textTable = isInput ? "tool_call_input_text" : "tool_call_result_text";
        string truncTable = isInput ? "tool_call_input_truncation" : "tool_call_result_truncation";

        object?[]? sizeRow = FindRowByKey(currentState, sizeTable, [("span_id", spanId)]);
        if (sizeRow is null)
        {
            return null;
        }

        TableMetadata sizeMeta = GetTableMetadata(sizeTable);
        long utf8Bytes = GetInt64(sizeMeta, sizeRow, "utf8_bytes");
        long characters = GetInt64(sizeMeta, sizeRow, "characters");
        string? plainText = ReadOptionalSingleString(currentState, textTable, "span_id", spanId, "text");
        string? sha = isInput ? ReadOptionalSingleString(currentState, "tool_call_input_fingerprint", "span_id", spanId, "sha256") : null;
        long? origBytes = ReadOptionalSingleInt64(currentState, truncTable, "span_id", spanId, "original_utf8_bytes");

        return new CollectedToolContent(plainText, utf8Bytes, characters, sha, origBytes);
    }

    private static CollectedAgentInvocation? MaterializeAgentInvocation(StoreState currentState, string spanId)
    {
        object?[]? row = FindRowByKey(currentState, "agent_invocation", [("span_id", spanId)]);
        if (row is null)
        {
            return null;
        }

        string agentName = GetString(GetTableMetadata("agent_invocation"), row, "agent_name");
        string? spawnId = ReadOptionalSingleString(currentState, "agent_invocation_spawn", "span_id", spanId, "spawning_record_id");
        long? outcomeInt = ReadOptionalSingleInt64(currentState, "agent_invocation_outcome", "span_id", spanId, "succeeded");
        bool? succeeded = outcomeInt.HasValue ? outcomeInt.Value == 1L : null;
        string? error = ReadOptionalSingleString(currentState, "agent_invocation_error", "span_id", spanId, "message");

        return new CollectedAgentInvocation(agentName, spawnId, succeeded, error);
    }

    private static CollectedRecordSignals MaterializeRecordSignals(StoreState currentState, string spanId)
    {
        string? errorMsg = ReadOptionalSingleString(currentState, "record_error", "span_id", spanId, "message");
        long? editFailures = ReadOptionalSingleInt64(currentState, "record_edit_failure", "span_id", spanId, "failure_count");
        string? correctionRule = ReadOptionalSingleString(currentState, "record_correction_rule", "span_id", spanId, "correction_rule");
        long? declaredWindow = ReadOptionalSingleInt64(currentState, "record_declared_context_window", "span_id", spanId, "declared_context_window");

        return new CollectedRecordSignals(errorMsg, editFailures, correctionRule, declaredWindow);
    }

    private static StoredSessionProjection MaterializeSessionProjection(StoreState currentState, object?[] row)
    {
        TableMetadata meta = GetTableMetadata("session_projection");
        string sessionId = GetString(meta, row, "session_id");
        string label = GetString(meta, row, "label");
        bool isSubagent = GetInt64(meta, row, "is_subagent") == 1L;
        int spanCount = (int)GetInt64(meta, row, "span_count");
        string startedAt = GetString(meta, row, "started_at_utc");
        string endedAt = GetString(meta, row, "ended_at_utc");

        string? parentSessionId = ReadOptionalSingleString(currentState, "session_projection_parent", "session_id", sessionId, "parent_session_id");
        string? agentName = ReadOptionalSingleString(currentState, "session_projection_agent", "session_id", sessionId, "agent_name");
        bool includedInParent = FindRowByKey(currentState, "session_projection_included_in_parent", [("session_id", sessionId)]) is not null;

        TableMetadata reqMeta = GetTableMetadata("session_request");
        List<string> requestSpans =
        [
            .. currentState.GetRows("session_request")
                .Where(r => string.Equals(GetString(reqMeta, r, "session_id"), sessionId, StringComparison.Ordinal))
                .OrderBy(r => (int)GetInt64(reqMeta, r, "position"))
                .Select(r => GetString(reqMeta, r, "request_span_id")),
        ];

        List<StoredSessionTurn> turns = MaterializeSessionTurns(currentState, sessionId);
        StoredSessionContext? context = MaterializeSessionContext(currentState, sessionId);
        string? contextUnmeasurable = ReadOptionalSingleString(currentState, "session_context_unmeasurable", "session_id", sessionId, "reason");
        StoredSessionSchema? schema = MaterializeSessionSchema(currentState, sessionId);
        string? schemaUnmeasurable = ReadOptionalSingleString(currentState, "session_schema_unmeasurable", "session_id", sessionId, "reason");

        TableMetadata subMeta = GetTableMetadata("session_timeline_subagent");
        List<string> subagentSpans =
        [
            .. currentState.GetRows("session_timeline_subagent")
                .Where(r => string.Equals(GetString(subMeta, r, "session_id"), sessionId, StringComparison.Ordinal))
                .Select(r => GetString(subMeta, r, "span_id")),
        ];

        TableMetadata auxMeta = GetTableMetadata("session_timeline_auxiliary");
        List<string> auxiliarySpans =
        [
            .. currentState.GetRows("session_timeline_auxiliary")
                .Where(r => string.Equals(GetString(auxMeta, r, "session_id"), sessionId, StringComparison.Ordinal))
                .Select(r => GetString(auxMeta, r, "span_id")),
        ];

        return new StoredSessionProjection(
            sessionId,
            label,
            isSubagent,
            spanCount,
            startedAt,
            endedAt,
            parentSessionId,
            agentName,
            includedInParent,
            requestSpans,
            turns,
            context,
            contextUnmeasurable,
            schema,
            schemaUnmeasurable,
            subagentSpans.Count > 0 ? subagentSpans : null,
            auxiliarySpans.Count > 0 ? auxiliarySpans : null);
    }

    private static List<StoredSessionTurn> MaterializeSessionTurns(StoreState currentState, string sessionId)
    {
        TableMetadata meta = GetTableMetadata("session_turn");
        return
        [
            .. currentState.GetRows("session_turn")
                .Where(r => string.Equals(GetString(meta, r, "session_id"), sessionId, StringComparison.Ordinal))
                .OrderBy(r => (int)GetInt64(meta, r, "position"))
                .Select(r => new StoredSessionTurn(
                    sessionId,
                    (int)GetInt64(meta, r, "position"),
                    GetString(meta, r, "span_id"))),
        ];
    }

    private static StoredSessionContext? MaterializeSessionContext(StoreState currentState, string sessionId)
    {
        object?[]? row = FindRowByKey(currentState, "session_context", [("session_id", sessionId)]);
        if (row is null)
        {
            return null;
        }

        TableMetadata meta = GetTableMetadata("session_context");
        TableMetadata obsMeta = GetTableMetadata("session_context_observed_part");
        List<string> observed =
        [
            .. currentState.GetRows("session_context_observed_part")
                .Where(r => string.Equals(GetString(obsMeta, r, "session_id"), sessionId, StringComparison.Ordinal))
                .Select(r => GetString(obsMeta, r, "bucket")),
        ];

        TableMetadata derMeta = GetTableMetadata("session_context_derived_count");
        List<StoredSessionContextDerivedCount> derived =
        [
            .. currentState.GetRows("session_context_derived_count")
                .Where(r => string.Equals(GetString(derMeta, r, "session_id"), sessionId, StringComparison.Ordinal))
                .Select(r => new StoredSessionContextDerivedCount(
                    GetString(derMeta, r, "counter"),
                    GetInt64(derMeta, r, "count"))),
        ];

        TableMetadata flagMeta = GetTableMetadata("session_context_flagged_turn");
        List<int> flagged =
        [
            .. currentState.GetRows("session_context_flagged_turn")
                .Where(r => string.Equals(GetString(flagMeta, r, "session_id"), sessionId, StringComparison.Ordinal))
                .OrderBy(r => (int)GetInt64(flagMeta, r, "turn_index"))
                .Select(r => (int)GetInt64(flagMeta, r, "turn_index")),
        ];

        TableMetadata turnMeta = GetTableMetadata("session_context_turn");
        List<StoredSessionContextTurn> turns = [];
        foreach (object?[] turnRow in currentState.GetRows("session_context_turn")
            .Where(r => string.Equals(GetString(turnMeta, r, "session_id"), sessionId, StringComparison.Ordinal))
            .OrderBy(r => (int)GetInt64(turnMeta, r, "position")))
        {
            long pos = GetInt64(turnMeta, turnRow, "position");
            turns.Add(MaterializeContextTurn(currentState, sessionId, pos, turnMeta, turnRow));
        }

        return new StoredSessionContext(
            GetInt64(meta, row, "context_limit"),
            GetString(meta, row, "context_limit_source"),
            GetInt64(meta, row, "residual_total"),
            GetDouble(meta, row, "fresh_jump_factor"),
            GetDouble(meta, row, "accumulation_rate"),
            observed,
            derived,
            flagged,
            turns);
    }

    private static StoredSessionContextTurn MaterializeContextTurn(
        StoreState currentState,
        string sessionId,
        long position,
        TableMetadata turnMeta,
        object?[] turnRow)
    {
        TableMetadata bucketMeta = GetTableMetadata("session_context_turn_bucket");
        List<StoredTurnBucketTokens> buckets =
        [
            .. currentState.GetRows("session_context_turn_bucket")
                .Where(r => string.Equals(GetString(bucketMeta, r, "session_id"), sessionId, StringComparison.Ordinal) &&
                            GetInt64(bucketMeta, r, "position") == position)
                .Select(r => new StoredTurnBucketTokens(GetString(bucketMeta, r, "bucket"), GetInt64(bucketMeta, r, "tokens"))),
        ];

        TableMetadata serverMeta = GetTableMetadata("session_context_turn_server_definitions");
        List<StoredTurnServerDefinitionTokens> serverDefs =
        [
            .. currentState.GetRows("session_context_turn_server_definitions")
                .Where(r => string.Equals(GetString(serverMeta, r, "session_id"), sessionId, StringComparison.Ordinal) &&
                            GetInt64(serverMeta, r, "position") == position)
                .Select(r => new StoredTurnServerDefinitionTokens(GetString(serverMeta, r, "server"), GetInt64(serverMeta, r, "tokens"))),
        ];

        object?[]? headroomRow = FindRowByKey(currentState, "session_context_turn_headroom", [("session_id", sessionId), ("position", position)]);
        TableMetadata headroomMeta = GetTableMetadata("session_context_turn_headroom");
        long? headroom = headroomRow is null ? null : GetInt64(headroomMeta, headroomRow, "headroom");
        double? pressure = headroomRow is null ? null : GetDouble(headroomMeta, headroomRow, "pressure");

        object?[]? accumRow = FindRowByKey(currentState, "session_context_turn_accumulation", [("session_id", sessionId), ("position", position)]);
        double? accumRate = accumRow is null ? null : GetDouble(GetTableMetadata("session_context_turn_accumulation"), accumRow, "rate");

        object?[]? strippedRow = FindRowByKey(currentState, "session_context_turn_stripped_instructions", [("session_id", sessionId), ("position", position)]);
        TableMetadata strippedMeta = GetTableMetadata("session_context_turn_stripped_instructions");
        long? strippedBlocks = strippedRow is null ? null : GetInt64(strippedMeta, strippedRow, "block_count");
        long? strippedTokens = strippedRow is null ? null : GetInt64(strippedMeta, strippedRow, "tokens");

        object?[]? jumpRow = FindRowByKey(currentState, "session_context_turn_fresh_jump", [("session_id", sessionId), ("position", position)]);
        TableMetadata jumpMeta = GetTableMetadata("session_context_turn_fresh_jump");
        long? prevFresh = jumpRow is null ? null : GetInt64(jumpMeta, jumpRow, "previous_fresh_input");
        double? jumpFactor = jumpRow is null ? null : GetDouble(jumpMeta, jumpRow, "factor");

        object?[]? nonResRow = FindRowByKey(currentState, "session_context_turn_non_resident", [("session_id", sessionId), ("position", position)]);
        long? nonResTokens = nonResRow is null ? null : GetInt64(GetTableMetadata("session_context_turn_non_resident"), nonResRow, "tokens");

        return new StoredSessionContextTurn(
            (int)position,
            GetInt64(turnMeta, turnRow, "input_tokens"),
            GetInt64(turnMeta, turnRow, "bucketed_tokens"),
            GetInt64(turnMeta, turnRow, "residual_tokens"),
            GetString(turnMeta, turnRow, "residual_attribution"),
            GetInt64(turnMeta, turnRow, "builtin_tool_definition_tokens"),
            GetInt64(turnMeta, turnRow, "fresh_input"),
            buckets,
            serverDefs,
            headroom,
            pressure,
            accumRate,
            strippedBlocks,
            strippedTokens,
            prevFresh,
            jumpFactor,
            nonResTokens);
    }

    private static StoredSessionSchema? MaterializeSessionSchema(StoreState currentState, string sessionId)
    {
        object?[]? row = FindRowByKey(currentState, "session_schema", [("session_id", sessionId)]);
        if (row is null)
        {
            return null;
        }

        TableMetadata meta = GetTableMetadata("session_schema");
        TableMetadata toolMeta = GetTableMetadata("session_schema_ranked_tool");
        TableMetadata toolServerMeta = GetTableMetadata("session_schema_ranked_tool_server");

        List<StoredSchemaRankedTool> rankedTools = [];
        foreach (object?[] toolRow in currentState.GetRows("session_schema_ranked_tool")
            .Where(r => string.Equals(GetString(toolMeta, r, "session_id"), sessionId, StringComparison.Ordinal))
            .OrderBy(r => (int)GetInt64(toolMeta, r, "position")))
        {
            long pos = GetInt64(toolMeta, toolRow, "position");
            object?[]? serverRow = FindRowByKey(currentState, "session_schema_ranked_tool_server", [("session_id", sessionId), ("position", pos)]);
            rankedTools.Add(new StoredSchemaRankedTool(
                (int)pos,
                GetString(toolMeta, toolRow, "tool_name"),
                GetInt64(toolMeta, toolRow, "cost_tokens"),
                GetInt64(toolMeta, toolRow, "invoked") == 1L,
                serverRow is null ? null : GetString(toolServerMeta, serverRow, "server_name")));
        }

        TableMetadata srvMeta = GetTableMetadata("session_schema_server");
        List<StoredSchemaServerTokens> servers =
        [
            .. currentState.GetRows("session_schema_server")
                .Where(r => string.Equals(GetString(srvMeta, r, "session_id"), sessionId, StringComparison.Ordinal))
                .Select(r => new StoredSchemaServerTokens(GetString(srvMeta, r, "server_name"), GetInt64(srvMeta, r, "tokens"))),
        ];

        long? unused = ReadOptionalSingleInt64(currentState, "session_schema_unused", "session_id", sessionId, "token_residencies");

        return new StoredSessionSchema(
            (int)GetInt64(meta, row, "invocation_count"),
            (int)GetInt64(meta, row, "turn_count"),
            rankedTools,
            servers,
            unused);
    }

    private static StoredRun MaterializeRun(StoreState currentState, object?[] row)
    {
        TableMetadata meta = GetTableMetadata("run");
        string runId = GetString(meta, row, "run_id");
        string? label = ReadOptionalSingleString(currentState, "run_label", "run_id", runId, "label");
        string? workingDir = ReadOptionalSingleString(currentState, "run_working_directory", "run_id", runId, "working_directory");

        object?[]? boundsRow = FindRowByKey(currentState, "run_bounds", [("run_id", runId)]);
        TableMetadata boundsMeta = GetTableMetadata("run_bounds");
        string? started = boundsRow is null ? null : GetString(boundsMeta, boundsRow, "started_at_utc");
        string? ended = boundsRow is null ? null : GetString(boundsMeta, boundsRow, "ended_at_utc");

        return new StoredRun(
            runId,
            GetString(meta, row, "harness_id"),
            GetString(meta, row, "grouping_basis"),
            GetString(meta, row, "grouping_rule"),
            (int)GetInt64(meta, row, "execution_count"),
            label,
            workingDir,
            started,
            ended);
    }

    private static StoredExecution MaterializeExecution(StoreState currentState, object?[] row)
    {
        TableMetadata meta = GetTableMetadata("execution");
        string executionId = GetString(meta, row, "execution_id");
        string? sessionId = ReadOptionalSingleString(currentState, "execution_session", "execution_id", executionId, "session_id");
        string? parentExecId = ReadOptionalSingleString(currentState, "execution_parent", "execution_id", executionId, "parent_execution_id");
        string? agentName = ReadOptionalSingleString(currentState, "execution_agent", "execution_id", executionId, "agent_name");

        object?[]? boundsRow = FindRowByKey(currentState, "execution_bounds", [("execution_id", executionId)]);
        TableMetadata boundsMeta = GetTableMetadata("execution_bounds");
        string? started = boundsRow is null ? null : GetString(boundsMeta, boundsRow, "started_at_utc");
        string? ended = boundsRow is null ? null : GetString(boundsMeta, boundsRow, "ended_at_utc");

        return new StoredExecution(
            executionId,
            GetString(meta, row, "run_id"),
            GetString(meta, row, "harness_id"),
            GetInt64(meta, row, "is_root") == 1L,
            sessionId,
            parentExecId,
            agentName,
            started,
            ended);
    }

    private static StoredHarnessRollup MaterializeHarnessRollup(StoreState currentState, object?[] row)
    {
        TableMetadata meta = GetTableMetadata("harness_rollup");
        string harnessId = GetString(meta, row, "harness_id");
        TableMetadata metricMeta = GetTableMetadata("harness_rollup_metric");
        List<StoredHarnessRollupMetric> metrics =
        [
            .. currentState.GetRows("harness_rollup_metric")
                .Where(r => string.Equals(GetString(metricMeta, r, "harness_id"), harnessId, StringComparison.Ordinal))
                .Select(r => new StoredHarnessRollupMetric(
                    GetString(metricMeta, r, "metric"),
                    GetDouble(metricMeta, r, "value"),
                    GetString(metricMeta, r, "basis"))),
        ];

        return new StoredHarnessRollup(
            harnessId,
            (int)GetInt64(meta, row, "session_count"),
            (int)GetInt64(meta, row, "run_count"),
            (int)GetInt64(meta, row, "execution_count"),
            (int)GetInt64(meta, row, "unknown_window_sessions"),
            (int)GetInt64(meta, row, "window_sessions_total"),
            metrics);
    }

    private static StoredFinding MaterializeFinding(StoreState currentState, object?[] row)
    {
        TableMetadata meta = GetTableMetadata("finding");
        string findingId = GetString(meta, row, "finding_id");

        TableMetadata linkMeta = GetTableMetadata("finding_evidence_link");
        List<FindingEvidenceLink> links =
        [
            .. currentState.GetRows("finding_evidence_link")
                .Where(r => string.Equals(GetString(linkMeta, r, "finding_id"), findingId, StringComparison.Ordinal))
                .OrderBy(r => (int)GetInt64(linkMeta, r, "position"))
                .Select(r => new FindingEvidenceLink(
                    GetString(linkMeta, r, "span_id"),
                    (int)GetInt64(linkMeta, r, "turn_index"),
                    GetString(linkMeta, r, "description"))),
        ];

        long? waste = ReadOptionalSingleInt64(currentState, "finding_estimated_waste", "finding_id", findingId, "tokens");

        object?[]? errRow = FindRowByKey(currentState, "finding_error_bar", [("finding_id", findingId)]);
        TableMetadata errMeta = GetTableMetadata("finding_error_bar");
        FindingErrorBar? errorBar = errRow is null
            ? null
            : new FindingErrorBar((long)GetDouble(errMeta, errRow, "lower"), (long)GetDouble(errMeta, errRow, "upper"));

        string? runId = ReadOptionalSingleString(currentState, "finding_run", "finding_id", findingId, "run_id");
        string? sessionId = ReadOptionalSingleString(currentState, "finding_session", "finding_id", findingId, "session_id");
        string? measurementClass = ReadOptionalSingleString(currentState, "finding_measurement_class", "finding_id", findingId, "measurement_class");
        string? confidenceBasis = ReadOptionalSingleString(currentState, "finding_confidence_basis", "finding_id", findingId, "confidence_basis");
        string? whatWouldRaise = ReadOptionalSingleString(currentState, "finding_what_would_raise_it", "finding_id", findingId, "text");

        object?[]? hazardRow = FindRowByKey(currentState, "finding_compaction_hazard", [("finding_id", findingId)]);
        TableMetadata hazardMeta = GetTableMetadata("finding_compaction_hazard");
        StoredCompactionHazard? hazard = hazardRow is null
            ? null
            : new StoredCompactionHazard(
                GetInt64(hazardMeta, hazardRow, "context_limit"),
                GetString(hazardMeta, hazardRow, "context_limit_source"));

        return new StoredFinding(
            findingId,
            GetString(meta, row, "detector_id"),
            GetString(meta, row, "title"),
            GetString(meta, row, "mechanism"),
            GetString(meta, row, "confidence"),
            GetString(meta, row, "recommendation"),
            GetString(meta, row, "outcome_risk_caveat"),
            GetDouble(meta, row, "rank_score"),
            links,
            waste,
            errorBar,
            runId,
            sessionId,
            measurementClass,
            confidenceBasis,
            whatWouldRaise,
            hazard);
    }

    private static StoredPrediction MaterializePrediction(StoreState currentState, object?[] row)
    {
        TableMetadata meta = GetTableMetadata("prediction");
        string predictionId = GetString(meta, row, "prediction_id");

        string? comparisonRunId = ReadOptionalSingleString(currentState, "prediction_comparison", "prediction_id", predictionId, "comparison_run_id");
        long? observedDelta = ReadOptionalSingleInt64(currentState, "prediction_observed_delta", "prediction_id", predictionId, "tokens");
        double? calibrationScore = ReadOptionalSingleDouble(currentState, "prediction_calibration", "prediction_id", predictionId, "score");

        return new StoredPrediction(
            predictionId,
            GetString(meta, row, "finding_id"),
            GetString(meta, row, "run_id"),
            GetInt64(meta, row, "predicted_waste_tokens"),
            GetDouble(meta, row, "confidence"),
            GetString(meta, row, "confidence_tier"),
            GetString(meta, row, "created_at_utc"),
            comparisonRunId,
            observedDelta,
            calibrationScore);
    }

    private static RefreshRunRow MaterializeRefreshRun(StoreState currentState, object?[] row)
    {
        TableMetadata meta = GetTableMetadata("refresh_run");
        string refreshRunId = GetString(meta, row, "refresh_run_id");
        string? completedAt = ReadOptionalSingleString(currentState, "refresh_run_completion", "refresh_run_id", refreshRunId, "completed_at_utc");
        string? summary = ReadOptionalSingleString(currentState, "refresh_run_summary", "refresh_run_id", refreshRunId, "summary");
        long? historyWeeks = ReadOptionalSingleInt64(currentState, "refresh_run_history_window", "refresh_run_id", refreshRunId, "history_weeks");

        return new RefreshRunRow(
            refreshRunId,
            GetString(meta, row, "started_at_utc"),
            completedAt,
            GetString(meta, row, "status"),
            (int)GetInt64(meta, row, "pid"),
            GetString(meta, row, "trigger"),
            summary,
            historyWeeks.HasValue ? (int)historyWeeks.Value : null);
    }

    private static SourceCheckpoint MaterializeSourceCheckpoint(StoreState currentState, object?[] row)
    {
        TableMetadata meta = GetTableMetadata("source_checkpoint");
        string harnessId = GetString(meta, row, "harness_id");
        string sourceKey = GetString(meta, row, "source_key");

        object?[]? successRow = FindRowByKey(currentState, "source_checkpoint_success", [("harness_id", harnessId), ("source_key", sourceKey)]);
        object?[]? errorRow = FindRowByKey(currentState, "source_checkpoint_error", [("harness_id", harnessId), ("source_key", sourceKey)]);

        return new SourceCheckpoint(
            harnessId,
            sourceKey,
            GetString(meta, row, "provider_id"),
            GetString(meta, row, "parser_id"),
            GetString(meta, row, "parser_contract_version"),
            GetString(meta, row, "format"),
            GetString(meta, row, "source_root_label"),
            GetString(meta, row, "revision_token"),
            GetString(meta, row, "covered_from_utc"),
            GetString(meta, row, "covered_through_utc"),
            GetString(meta, row, "last_attempt_utc"),
            successRow is null ? null : GetString(GetTableMetadata("source_checkpoint_success"), successRow, "last_success_utc"),
            GetString(meta, row, "last_status"),
            errorRow is null ? null : GetString(GetTableMetadata("source_checkpoint_error"), errorRow, "last_error_code"),
            (int)GetInt64(meta, row, "unit_count"),
            (int)GetInt64(meta, row, "record_count"));
    }

    private static StoredIngestProblem MaterializeIngestProblem(StoreState currentState, object?[] row)
    {
        TableMetadata meta = GetTableMetadata("ingest_problem");
        long problemId = GetInt64(meta, row, "problem_id");

        return new StoredIngestProblem(
            problemId,
            new CollectedProblem(
                GetString(meta, row, "problem_key"),
                GetString(meta, row, "severity"),
                GetString(meta, row, "code"),
                GetString(meta, row, "message"),
                ReadOptionalSingleString(currentState, "ingest_problem_span", "problem_id", problemId, "span_id"),
                ReadOptionalSingleString(currentState, "ingest_problem_location", "problem_id", problemId, "location"),
                ReadOptionalSingleString(currentState, "ingest_problem_session", "problem_id", problemId, "session_id"),
                ReadOptionalSingleString(currentState, "ingest_problem_harness", "problem_id", problemId, "harness_id"),
                ReadOptionalSingleString(currentState, "ingest_problem_timestamp", "problem_id", problemId, "timestamp_utc"),
                ReadOptionalSingleString(currentState, "ingest_problem_stack", "problem_id", problemId, "stack")));
    }

    private static StoredQuarantinedSpan MaterializeQuarantinedSpan(StoreState currentState, object?[] row)
    {
        TableMetadata meta = GetTableMetadata("quarantined_span");
        string spanId = GetString(meta, row, "span_id");
        TableMetadata nsMeta = GetTableMetadata("quarantined_span_namespace");
        List<string> namespaces =
        [
            .. currentState.GetRows("quarantined_span_namespace")
                .Where(r => string.Equals(GetString(nsMeta, r, "span_id"), spanId, StringComparison.Ordinal))
                .Select(r => GetString(nsMeta, r, "namespace_name")),
        ];

        return new StoredQuarantinedSpan(
            spanId,
            GetString(meta, row, "reason"),
            namespaces,
            ReadOptionalSingleString(currentState, "quarantined_span_source", "span_id", spanId, "source"),
            ReadOptionalSingleString(currentState, "quarantined_span_name", "span_id", spanId, "name"),
            ReadOptionalSingleString(currentState, "quarantined_span_timestamp", "span_id", spanId, "timestamp_utc"),
            ReadOptionalSingleString(currentState, "quarantined_span_harness", "span_id", spanId, "harness_id"));
    }

    private static StoredMetricPoint MaterializeMetricPoint(StoreState currentState, object?[] row)
    {
        TableMetadata meta = GetTableMetadata("metric_point");
        long pointId = GetInt64(meta, row, "point_id");
        long? temp = ReadOptionalSingleInt64(currentState, "metric_point_temporality", "point_id", pointId, "aggregation_temporality");

        return new StoredMetricPoint(
            GetString(meta, row, "point_key"),
            GetString(meta, row, "name"),
            GetString(meta, row, "kind"),
            ReadOptionalSingleString(currentState, "metric_point_unit", "point_id", pointId, "unit"),
            temp.HasValue ? (int)temp.Value : null,
            ReadOptionalSingleString(currentState, "metric_point_time", "point_id", pointId, "time_unix_nano"),
            ReadOptionalSingleString(currentState, "metric_point_start_time", "point_id", pointId, "start_time_unix_nano"),
            ReadOptionalSingleString(currentState, "metric_point_timestamp", "point_id", pointId, "timestamp_utc"),
            ReadOptionalSingleDouble(currentState, "metric_point_value", "point_id", pointId, "value"),
            ReadOptionalSingleInt64(currentState, "metric_point_count", "point_id", pointId, "point_count"),
            ReadOptionalSingleDouble(currentState, "metric_point_sum", "point_id", pointId, "point_sum"),
            ReadOptionalSingleString(currentState, "metric_point_source", "point_id", pointId, "source"));
    }

    private static StoredFieldsView BuildStoredFieldsView(StoreState currentState, string sessionKey)
    {
        SessionScope scope = ResolveSessionScope(currentState, sessionKey);
        Dictionary<string, List<object?[]>> scopedRowsByTable = new(RelationalSchemaCatalog.Tables.Count, StringComparer.Ordinal);
        List<StoredTableFields> tableEntries = new(RelationalSchemaCatalog.Tables.Count);

        foreach (TableDefinition table in RelationalSchemaCatalog.Tables)
        {
            List<object?[]> scopedRows = FilterRowsForSessionScope(currentState, table, scope);
            scopedRowsByTable[table.Name] = scopedRows;

            List<string> columnNames = [.. table.Columns.Select(static c => c.Name)];
            List<StoredFieldRow> renderedRows = [.. scopedRows.Select(r => ToStoredFieldRow(table, r))];
            List<string> missingParentKeys = ComputeMissingParentKeys(table, scopedRows, scopedRowsByTable);

            tableEntries.Add(new StoredTableFields(table.Name, columnNames, renderedRows, missingParentKeys));
        }

        return new StoredFieldsView(sessionKey, tableEntries);
    }

    /// <summary>
    /// Derives one session's sessions-list figures from its session row, content-free records,
    /// stored projection, and ingest problems. The turn loop walks the same scalar columns the
    /// retired session-aggregate read walked; the fields the sessions list renders are kept and
    /// the content-bearing aggregate fields are deliberately absent.
    /// </summary>
    private static SessionListFigure BuildSessionListFigure(
        StoreState currentState,
        CollectedSession session,
        IReadOnlyList<CollectedRecord> records,
        IModelPricingLookup? pricing)
    {
        string sessionKey = session.SessionKey;
        string harnessId = session.HarnessId;

        HashSet<string> sessionSpanIds = new(StringComparer.Ordinal);
        foreach (CollectedRecord record in records)
        {
            _ = sessionSpanIds.Add(record.RecordId);
        }

        StoredSessionProjection? projection = GetSessionProjectionFromState(currentState, sessionKey);
        List<(int Position, CollectedRecord Record)> turnEntries = ResolveSessionTurnEntries(currentState, sessionKey, records);

        List<CostBlock> turnPublishedBlocks = new(turnEntries.Count);
        List<CostBlock> valuedTurnPublishedBlocks = [];
        List<CostBlock> valuedTurnHarnessBlocks = [];
        List<string> models = [];
        HashSet<string> seenModels = new(StringComparer.Ordinal);
        long totalInput = 0;
        bool hasInput = false;
        long totalOutput = 0;
        bool hasOutput = false;

        foreach ((int position, CollectedRecord turnRecord) in turnEntries)
        {
            ReadTimeTurnSummary turnSummary = BuildTurnSummary(
                turnRecord,
                position,
                harnessId,
                pricing,
                out CostBlock publishedBlock,
                out CostBlock? harnessBlock);

            turnPublishedBlocks.Add(publishedBlock);
            if (publishedBlock.Value is double publishedValue && double.IsFinite(publishedValue))
            {
                valuedTurnPublishedBlocks.Add(publishedBlock);
            }

            if (harnessBlock is not null && harnessBlock.Value is double harnessValue && double.IsFinite(harnessValue))
            {
                valuedTurnHarnessBlocks.Add(harnessBlock);
            }

            if (!string.IsNullOrEmpty(turnRecord.Model) && seenModels.Add(turnRecord.Model))
            {
                models.Add(turnRecord.Model);
            }

            if (turnSummary.InputTokens.HasValue)
            {
                totalInput += turnSummary.InputTokens.Value;
                hasInput = true;
            }

            if (turnSummary.OutputTokens.HasValue)
            {
                totalOutput += turnSummary.OutputTokens.Value;
                hasOutput = true;
            }
        }

        List<CollectedRecord> rootRecords =
        [
            .. records
                .Where(r => r.ParentRecordId is null ||
                            string.Equals(r.ParentRecordId, r.RecordId, StringComparison.Ordinal) ||
                            !sessionSpanIds.Contains(r.ParentRecordId))
                .OrderBy(static r => r.StartedAt)
                .ThenBy(static r => r.RecordId, StringComparer.Ordinal),
        ];

        List<Problem> extraProblems = [];

        List<CostBlock> reportedRecordBlocks = [];
        foreach (CollectedRecord record in records)
        {
            if (record.ReportedCost is null)
            {
                continue;
            }

            IReadOnlyDictionary<string, double>? byModel = !string.IsNullOrEmpty(record.Model)
                ? new Dictionary<string, double>(StringComparer.Ordinal) { [record.Model] = record.ReportedCost.Amount }
                : null;
            reportedRecordBlocks.Add(new CostBlock(
                CostBasis.Harness,
                CostStatus.Priced,
                record.ReportedCost.Amount,
                record.ReportedCost.Currency,
                byModel));
        }

        ReadTimeCostSummary? sessionReportedCost = reportedRecordBlocks.Count > 0
            ? AggregateCostBlocks(reportedRecordBlocks, CostBasis.Harness, extraProblems)
            : null;

        ReadTimeCostSummary sessionPublishedCost = turnPublishedBlocks.Count > 0
            ? AggregateCostBlocks(turnPublishedBlocks, CostBasis.Published, extraProblems)
            : new ReadTimeCostSummary(CostBasis.Unknown, CostStatus.NoRate, null, null, []);

        ReadTimeCostBasisMismatch? basisMismatch = null;
        if (valuedTurnHarnessBlocks.Count > 0 && valuedTurnPublishedBlocks.Count > 0)
        {
            List<CostBlock> combined = [.. valuedTurnHarnessBlocks, .. valuedTurnPublishedBlocks];
            CostTotalResult mismatchTotal = CanonPricing.SumCosts(combined);
            CostBasisMismatch? domainMismatch = CanonPricing.CostBasisMismatchOf(combined, mismatchTotal);
            if (domainMismatch is not null)
            {
                List<ReadTimeBasisTotal> totalsByBasis =
                [
                    .. domainMismatch.TotalsByBasis
                        .OrderBy(static kv => kv.Key, StringComparer.Ordinal)
                        .Select(static kv => new ReadTimeBasisTotal(kv.Key, kv.Value)),
                ];
                basisMismatch = new ReadTimeCostBasisMismatch(
                    domainMismatch.Code,
                    domainMismatch.Message,
                    domainMismatch.Bases,
                    totalsByBasis);
            }

            if (mismatchTotal.Problem is not null)
            {
                extraProblems.Add(mismatchTotal.Problem);
            }
        }

        int requestCount = projection is not null && projection.RequestSpanIds.Count > 0
            ? projection.RequestSpanIds.Count
            : rootRecords.Count;

        List<Problem> problems = CollectSessionProblems(currentState, sessionKey, sessionSpanIds);
        problems.AddRange(extraProblems);

        return new SessionListFigure(
            SessionKey: sessionKey,
            HarnessId: harnessId,
            Repo: session.Workspace?.Repository,
            Branch: session.Workspace?.Branch,
            TurnCount: turnEntries.Count,
            RequestCount: requestCount,
            TotalInput: hasInput ? totalInput : null,
            TotalOutput: hasOutput ? totalOutput : null,
            ReportedCost: sessionReportedCost,
            PublishedCost: sessionPublishedCost,
            CostBasisMismatch: basisMismatch,
            Models: models,
            ProblemsCount: problems.Count);
    }

    /// <summary>
    /// Collects a session's records from state without materializing any content-bearing child
    /// row, so <see cref="SessionListFigures"/> never counts content bytes on the fake.
    /// </summary>
    private static List<CollectedRecord> CollectSessionRecordsContentFree(StoreState currentState, string sessionKey)
    {
        HashSet<string> sessionSpans = ResolveSessionSpanIds(currentState, sessionKey);
        if (sessionSpans.Count == 0)
        {
            return [];
        }

        TableMetadata recordMeta = GetTableMetadata("record");
        List<CollectedRecord> records = [];
        foreach (object?[] row in currentState.GetRows("record"))
        {
            if (sessionSpans.Contains(GetString(recordMeta, row, "span_id")))
            {
                records.Add(MaterializeRecordContentFree(currentState, row));
            }
        }

        return records;
    }

    /// <summary>
    /// Materializes a record's scalar columns and token counts — the columns the sessions-list
    /// figures derive from — without any content-bearing child row: parts, tool calls, tool
    /// definitions, agent invocations, and skills stay absent, and signals stay absent because
    /// no list figure reads them.
    /// </summary>
    /// <summary>
    /// Derives one session's bounded summary figures, tool residency rows, and request metadata
    /// from content-free record scalars, its stored projection, and ingest diagnostics: the
    /// figures the retired session-aggregate read computed, minus every content-bearing
    /// column.
    /// </summary>
    private static SessionFigureSet? BuildSessionFigures(
        StoreState currentState,
        string sessionKey,
        List<CollectedRecord> records,
        IModelPricingLookup? pricing)
    {
        object?[]? sessionRow = FindRowByKey(currentState, "session", [("session_id", sessionKey)]);
        if (sessionRow is null)
        {
            return null;
        }

        CollectedSession session = MaterializeSession(currentState, sessionRow);
        string harnessId = session.HarnessId;

        HashSet<string> sessionSpanIds = new(StringComparer.Ordinal);
        Dictionary<string, CollectedRecord> recordsBySpanId = new(records.Count, StringComparer.Ordinal);
        foreach (CollectedRecord record in records)
        {
            _ = sessionSpanIds.Add(record.RecordId);
            recordsBySpanId[record.RecordId] = record;
        }

        StoredSessionProjection? projection = GetSessionProjectionFromState(currentState, sessionKey);
        List<(int Position, CollectedRecord Record)> turnEntries = ResolveSessionTurnEntries(currentState, sessionKey, records);

        List<CostBlock> turnPublishedBlocks = new(turnEntries.Count);
        List<CostBlock> valuedTurnPublishedBlocks = [];
        List<CostBlock> valuedTurnHarnessBlocks = [];
        List<string> models = [];
        HashSet<string> seenModels = new(StringComparer.Ordinal);
        long totalInput = 0;
        bool hasInput = false;
        long totalOutput = 0;
        bool hasOutput = false;
        long totalCacheRead = 0;
        bool hasCacheRead = false;
        long totalCacheCreation = 0;
        bool hasCacheCreation = false;
        int cacheCreationTurns = 0;
        List<ReadTimeTurnReconciliation> reconciliation = new(turnEntries.Count);
        int unmeasuredInputTurns = 0;

        foreach ((int position, CollectedRecord turnRecord) in turnEntries)
        {
            ReadTimeTurnSummary turnSummary = BuildTurnSummary(
                turnRecord,
                position,
                harnessId,
                pricing,
                out CostBlock publishedBlock,
                out CostBlock? harnessBlock);

            if (!turnSummary.InputTokens.HasValue)
            {
                unmeasuredInputTurns++;
            }

            turnPublishedBlocks.Add(publishedBlock);
            if (publishedBlock.Value is double publishedValue && double.IsFinite(publishedValue))
            {
                valuedTurnPublishedBlocks.Add(publishedBlock);
            }

            if (harnessBlock is not null && harnessBlock.Value is double harnessValue && double.IsFinite(harnessValue))
            {
                valuedTurnHarnessBlocks.Add(harnessBlock);
            }

            if (!string.IsNullOrEmpty(turnRecord.Model) && seenModels.Add(turnRecord.Model))
            {
                models.Add(turnRecord.Model);
            }

            if (turnSummary.InputTokens.HasValue)
            {
                totalInput += turnSummary.InputTokens.Value;
                hasInput = true;
            }

            if (turnSummary.OutputTokens.HasValue)
            {
                totalOutput += turnSummary.OutputTokens.Value;
                hasOutput = true;
            }

            if (turnSummary.CacheReadTokens.HasValue)
            {
                totalCacheRead += turnSummary.CacheReadTokens.Value;
                hasCacheRead = true;
            }

            if (turnSummary.CacheCreationTokens.HasValue)
            {
                totalCacheCreation += turnSummary.CacheCreationTokens.Value;
                hasCacheCreation = true;
                if (turnSummary.CacheCreationTokens.Value > 0)
                {
                    cacheCreationTurns++;
                }
            }

            bool? inputMatch = turnRecord.ReportedInputTokens.HasValue && turnSummary.InputTokens.HasValue
                ? turnRecord.ReportedInputTokens.Value == turnSummary.InputTokens.Value
                : null;
            bool? outputMatch = turnRecord.ReportedOutputTokens.HasValue && turnSummary.OutputTokens.HasValue
                ? turnRecord.ReportedOutputTokens.Value == turnSummary.OutputTokens.Value
                : null;
            reconciliation.Add(new ReadTimeTurnReconciliation(
                turnRecord.RecordId,
                turnRecord.ReportedInputTokens,
                turnSummary.InputTokens,
                inputMatch,
                turnRecord.ReportedOutputTokens,
                turnSummary.OutputTokens,
                outputMatch));
        }

        List<CollectedRecord> rootRecords =
        [
            .. records
                .Where(r => r.ParentRecordId is null ||
                            string.Equals(r.ParentRecordId, r.RecordId, StringComparison.Ordinal) ||
                            !sessionSpanIds.Contains(r.ParentRecordId))
                .OrderBy(static r => r.StartedAt)
                .ThenBy(static r => r.RecordId, StringComparer.Ordinal),
        ];

        bool hasNesting = records.Any(r =>
            r.ParentRecordId is not null &&
            !string.Equals(r.ParentRecordId, r.RecordId, StringComparison.Ordinal) &&
            sessionSpanIds.Contains(r.ParentRecordId));

        IReadOnlyList<CollectedRecord> durationCandidates = hasNesting ? rootRecords : records;
        long totalDuration = 0;
        bool hasDuration = false;
        foreach (CollectedRecord candidate in durationCandidates)
        {
            long? ms = ComputeRecordDurationMs(candidate);
            if (ms.HasValue)
            {
                totalDuration += ms.Value;
                hasDuration = true;
            }
        }

        List<Problem> extraProblems = [];

        List<CostBlock> reportedRecordBlocks = [];
        foreach (CollectedRecord record in records)
        {
            if (record.ReportedCost is null)
            {
                continue;
            }

            IReadOnlyDictionary<string, double>? byModel = !string.IsNullOrEmpty(record.Model)
                ? new Dictionary<string, double>(StringComparer.Ordinal) { [record.Model] = record.ReportedCost.Amount }
                : null;
            reportedRecordBlocks.Add(new CostBlock(
                CostBasis.Harness,
                CostStatus.Priced,
                record.ReportedCost.Amount,
                record.ReportedCost.Currency,
                byModel));
        }

        ReadTimeCostSummary? sessionReportedCost = reportedRecordBlocks.Count > 0
            ? AggregateCostBlocks(reportedRecordBlocks, CostBasis.Harness, extraProblems)
            : null;

        ReadTimeCostSummary sessionPublishedCost = turnPublishedBlocks.Count > 0
            ? AggregateCostBlocks(turnPublishedBlocks, CostBasis.Published, extraProblems)
            : new ReadTimeCostSummary(CostBasis.Unknown, CostStatus.NoRate, null, null, []);

        ReadTimeCostBasisMismatch? basisMismatch = null;
        if (valuedTurnHarnessBlocks.Count > 0 && valuedTurnPublishedBlocks.Count > 0)
        {
            List<CostBlock> combined = [.. valuedTurnHarnessBlocks, .. valuedTurnPublishedBlocks];
            CostTotalResult mismatchTotal = CanonPricing.SumCosts(combined);
            CostBasisMismatch? domainMismatch = CanonPricing.CostBasisMismatchOf(combined, mismatchTotal);
            if (domainMismatch is not null)
            {
                List<ReadTimeBasisTotal> totalsByBasis =
                [
                    .. domainMismatch.TotalsByBasis
                        .OrderBy(static kv => kv.Key, StringComparer.Ordinal)
                        .Select(static kv => new ReadTimeBasisTotal(kv.Key, kv.Value)),
                ];
                basisMismatch = new ReadTimeCostBasisMismatch(
                    domainMismatch.Code,
                    domainMismatch.Message,
                    domainMismatch.Bases,
                    totalsByBasis);
            }

            if (mismatchTotal.Problem is not null)
            {
                extraProblems.Add(mismatchTotal.Problem);
            }
        }

        long? nullableTotalInput = hasInput ? totalInput : null;
        long? nullableTotalOutput = hasOutput ? totalOutput : null;
        long? nullableTotalCacheRead = hasCacheRead ? totalCacheRead : null;
        long? nullableTotalCacheCreation = hasCacheCreation ? totalCacheCreation : null;

        double? cacheHitRatio = nullableTotalInput is > 0 && nullableTotalCacheRead.HasValue
            ? (double)nullableTotalCacheRead.Value / nullableTotalInput.Value
            : null;
        int? cacheCreationCoverage = nullableTotalInput is > 0 && nullableTotalCacheCreation.HasValue && turnEntries.Count > 0
            ? cacheCreationTurns
            : null;

        int toolCallTotal = 0;
        List<string> toolsInvoked = [];
        HashSet<string> seenInvokedTools = new(StringComparer.Ordinal);
        Dictionary<string, int> invCounts = new(StringComparer.Ordinal);
        Dictionary<string, int> errCounts = new(StringComparer.Ordinal);
        foreach (CollectedRecord record in records)
        {
            if (record.Tool is null)
            {
                continue;
            }

            toolCallTotal++;
            string toolName = record.Tool.ToolName;
            if (seenInvokedTools.Add(toolName))
            {
                toolsInvoked.Add(toolName);
            }

            invCounts[toolName] = invCounts.GetValueOrDefault(toolName) + 1;
            if (record.Status == RecordStatus.Error ||
                record.Tool.Succeeded == false ||
                record.Tool.Error is not null)
            {
                errCounts[toolName] = errCounts.GetValueOrDefault(toolName) + 1;
            }
        }

        int? toolCalls = toolCallTotal > 0 ? toolCallTotal : null;

        List<string> toolsOffered = [];
        HashSet<string> seenOfferedTools = new(StringComparer.Ordinal);
        Dictionary<string, (long SchemaTokens, string? Server, HashSet<string> SpanIds)> defStats = new(StringComparer.Ordinal);
        int toolDefinitionRowCount = 0;
        foreach (CollectedRecord record in records)
        {
            foreach (CollectedToolDefinition def in record.ToolDefinitions)
            {
                toolDefinitionRowCount++;
                if (seenOfferedTools.Add(def.ToolName))
                {
                    toolsOffered.Add(def.ToolName);
                }

                if (!defStats.TryGetValue(def.ToolName, out (long SchemaTokens, string? Server, HashSet<string> SpanIds) existing))
                {
                    defStats[def.ToolName] = (
                        def.SchemaTokens,
                        def.Server,
                        new HashSet<string>(StringComparer.Ordinal) { record.RecordId });
                }
                else
                {
                    _ = existing.SpanIds.Add(record.RecordId);
                    defStats[def.ToolName] = (existing.SchemaTokens, existing.Server ?? def.Server, existing.SpanIds);
                }
            }
        }

        List<ReadTimeSessionToolSummary> tools = BuildSessionToolSummaries(
            projection,
            turnEntries.Count,
            defStats,
            toolsInvoked,
            invCounts,
            errCounts);
        int reconciliationTotal = 0;
        int reconciliationMismatched = 0;
        foreach (ReadTimeTurnReconciliation row in reconciliation)
        {
            if (!row.InputMatch.HasValue || !row.OutputMatch.HasValue)
            {
                continue;
            }

            reconciliationTotal++;
            if (!row.InputMatch.Value || !row.OutputMatch.Value)
            {
                reconciliationMismatched++;
            }
        }

        IReadOnlyList<string> requestSpanIds = projection is not null && projection.RequestSpanIds.Count > 0
            ? projection.RequestSpanIds
            : [.. rootRecords.Select(static r => r.RecordId)];
        int requestCount = requestSpanIds.Count;
        List<ReadTimeSessionRequestSummary> requests = BuildRequestSummaries(
            requestSpanIds,
            records,
            recordsBySpanId,
            turnEntries);

        List<Problem> problems = CollectSessionProblems(currentState, sessionKey, sessionSpanIds);
        problems.AddRange(extraProblems);

        List<ReadTimeSessionServerSummary> servers = BuildSessionServerSummaries(projection, tools);
        ReadTimeContextBucketSummary? firstContextBucket = null;
        ReadTimeContextBucketSummary? lastContextBucket = null;
        if (projection?.Context?.Turns is { Count: > 0 } contextTurns)
        {
            List<StoredSessionContextTurn> orderedContext = [.. contextTurns.OrderBy(static t => t.Position)];
            firstContextBucket = BuildContextBucketSummary(orderedContext[0], turnEntries);
            lastContextBucket = BuildContextBucketSummary(orderedContext[^1], turnEntries);
        }

        UnusedSchemaRange? unusedRange = ResolveUnusedRange(projection, harnessId, models, pricing);

        return new SessionFigureSet(
            new SessionSummaryFigure(
                SessionKey: sessionKey,
                HarnessId: harnessId,
                TurnCount: turnEntries.Count,
                RequestCount: requestCount,
                TotalInput: nullableTotalInput,
                TotalOutput: nullableTotalOutput,
                TotalCacheRead: nullableTotalCacheRead,
                TotalCacheCreation: nullableTotalCacheCreation,
                DurationMs: hasDuration ? totalDuration : null,
                Models: models,
                CacheHitRatio: cacheHitRatio,
                CacheCreationCoverage: cacheCreationCoverage,
                ToolCalls: toolCalls,
                ToolsInvoked: toolsInvoked,
                HasToolDefinitions: toolDefinitionRowCount > 0,
                ToolsOffered: toolsOffered,
                ReportedCost: sessionReportedCost,
                PublishedCost: sessionPublishedCost,
                CostBasisMismatch: basisMismatch,
                Coverage: new ReadTimeSessionCoverage(
                    SchemaMeasurable: projection?.Schema is not null,
                    ContextMeasurable: projection?.Context is not null),
                Problems: problems,
                SkillInvocations: [],
                ToolRowsCount: tools.Count,
                UnmeasuredInputTurns: unmeasuredInputTurns,
                ReconciliationTotal: reconciliationTotal,
                ReconciliationMismatched: reconciliationMismatched),
            tools,
            requests,
            servers,
            reconciliation,
            firstContextBucket,
            lastContextBucket,
            unusedRange);
    }

    /// <summary>One session's content-free figures with the detail page's tool and request rows.</summary>
    private sealed record SessionFigureSet(
        SessionSummaryFigure Summary,
        IReadOnlyList<ReadTimeSessionToolSummary> Tools,
        IReadOnlyList<ReadTimeSessionRequestSummary> Requests,
        IReadOnlyList<ReadTimeSessionServerSummary> Servers,
        IReadOnlyList<ReadTimeTurnReconciliation> Reconciliation,
        ReadTimeContextBucketSummary? FirstContextBucket,
        ReadTimeContextBucketSummary? LastContextBucket,
        UnusedSchemaRange? UnusedRange);

    private static IReadOnlyList<ReadTimeTurnSummary> BuildSessionTurnFigures(
        StoreState currentState,
        string sessionKey,
        List<CollectedRecord> records,
        string harnessId,
        IModelPricingLookup? pricing)
    {
        List<(int Position, CollectedRecord Record)> turnEntries = ResolveSessionTurnEntries(currentState, sessionKey, records);
        List<ReadTimeTurnSummary> summaries = new(turnEntries.Count);
        foreach ((int position, CollectedRecord turnRecord) in turnEntries)
        {
            summaries.Add(BuildTurnSummary(turnRecord, position, harnessId, pricing, out _, out _));
        }

        return summaries;
    }

    private static CollectedRecord MaterializeRecordContentFree(StoreState currentState, object?[] recordRow)
    {
        TableMetadata meta = GetTableMetadata("record");
        string spanId = GetString(meta, recordRow, "span_id");
        string harnessId = GetString(meta, recordRow, "harness_id");
        RecordOp op = ParseRecordOp(GetString(meta, recordRow, "op"));
        RecordKind kind = ParseRecordKind(GetString(meta, recordRow, "kind"));
        string name = GetString(meta, recordRow, "name");
        DateTimeOffset startedAt = ParseTimestamp(GetString(meta, recordRow, "started_at_utc"));
        RecordStatus status = ParseRecordStatus(GetString(meta, recordRow, "status"));

        string? sessionKey = ReadOptionalSingleString(currentState, "record_session_link", "span_id", spanId, "session_key");
        string? parentSpanId = ReadOptionalSingleString(currentState, "record_parent", "span_id", spanId, "parent_span_id");
        string? traceId = ReadOptionalSingleString(currentState, "record_trace", "span_id", spanId, "trace_id");
        string? model = ReadOptionalSingleString(currentState, "record_model", "span_id", spanId, "model_id");
        string? endedRaw = ReadOptionalSingleString(currentState, "record_end", "span_id", spanId, "ended_at_utc");
        DateTimeOffset? endedAt = endedRaw is null ? null : ParseTimestamp(endedRaw);
        long? reportedDurationMs = ReadOptionalSingleInt64(currentState, "record_reported_duration", "span_id", spanId, "duration_ms");
        string? agentInstanceId = ReadOptionalSingleString(currentState, "record_agent_instance", "span_id", spanId, "agent_instance_id");

        TableMetadata tokenMeta = GetTableMetadata("record_token_count");
        List<CollectedTokenCount> tokens =
        [
            .. currentState.GetRows("record_token_count")
                .Where(r => string.Equals(GetString(tokenMeta, r, "span_id"), spanId, StringComparison.Ordinal))
                .Select(r => new CollectedTokenCount(
                    ParseTokenClass(GetString(tokenMeta, r, "token_class")),
                    GetInt64(tokenMeta, r, "token_count"))),
        ];

        object?[]? costRow = FindRowByKey(currentState, "record_reported_cost", [("span_id", spanId)]);
        CollectedCost? reportedCost = costRow is null
            ? null
            : new CollectedCost(
                GetDouble(GetTableMetadata("record_reported_cost"), costRow, "amount"),
                GetString(GetTableMetadata("record_reported_cost"), costRow, "currency"));

        long? reportedInput = ReadOptionalReportedTotal(currentState, spanId, "input");
        long? reportedOutput = ReadOptionalReportedTotal(currentState, spanId, "output");

        TableMetadata defMeta = GetTableMetadata("tool_definition");
        List<CollectedToolDefinition> toolDefs =
        [
            .. currentState.GetRows("tool_definition")
                .Where(r => string.Equals(GetString(defMeta, r, "span_id"), spanId, StringComparison.Ordinal))
                .OrderBy(r => (int)GetInt64(defMeta, r, "position"))
                .Select(r => MaterializeToolDefinition(currentState, r)),
        ];

        return new CollectedRecord(
            spanId,
            harnessId,
            op,
            kind,
            name,
            startedAt,
            status,
            sessionKey,
            parentSpanId,
            traceId,
            model,
            endedAt,
            reportedDurationMs,
            tokens,
            reportedCost,
            Parts: [],
            Tool: MaterializeToolCallContentFree(currentState, spanId),
            Agent: null,
            SkillsInvoked: [],
            agentInstanceId,
            toolDefs,
            MaterializeRecordSignals(currentState, spanId),
            reportedInput,
            reportedOutput);
    }

    /// <summary>
    /// Materializes a tool call's metadata with no input or result text, the shape the
    /// content-free record read carries.
    /// </summary>
    private static CollectedToolCall? MaterializeToolCallContentFree(StoreState currentState, string spanId)
    {
        CollectedToolCall? call = MaterializeToolCall(currentState, spanId);
        if (call is null)
        {
            return null;
        }

        return call with
        {
            Input = call.Input is null ? null : call.Input with { PlainText = null },
            Result = call.Result is null ? null : call.Result with { PlainText = null },
        };
    }

    private static StoredSessionProjection? GetSessionProjectionFromState(StoreState currentState, string sessionId)
    {
        object?[]? row = FindRowByKey(currentState, "session_projection", [("session_id", sessionId)]);
        return row is null ? null : MaterializeSessionProjection(currentState, row);
    }

    private static List<(int Position, CollectedRecord Record)> ResolveSessionTurnEntries(
        StoreState currentState,
        string sessionKey,
        IReadOnlyList<CollectedRecord> records)
    {
        Dictionary<string, CollectedRecord> bySpan = new(records.Count, StringComparer.Ordinal);
        foreach (CollectedRecord record in records)
        {
            bySpan[record.RecordId] = record;
        }

        List<StoredSessionTurn> projectedTurns = MaterializeSessionTurns(currentState, sessionKey);
        if (projectedTurns.Count > 0)
        {
            List<(int Position, CollectedRecord Record)> resolved = new(projectedTurns.Count);
            foreach (StoredSessionTurn turn in projectedTurns)
            {
                if (bySpan.TryGetValue(turn.SpanId, out CollectedRecord? record))
                {
                    resolved.Add((turn.Position, record));
                }
            }

            return resolved;
        }

        List<CollectedRecord> llmRecords =
        [
            .. records
                .Where(static r => r.Op == RecordOp.LlmInvoke)
                .OrderBy(static r => r.StartedAt)
                .ThenBy(static r => r.RecordId, StringComparer.Ordinal),
        ];

        List<(int Position, CollectedRecord Record)> fallback = new(llmRecords.Count);
        for (int index = 0; index < llmRecords.Count; index++)
        {
            fallback.Add((index, llmRecords[index]));
        }

        return fallback;
    }

    private static ReadTimeTurnSummary BuildTurnSummary(
        CollectedRecord record,
        int position,
        string harnessId,
        IModelPricingLookup? pricing,
        out CostBlock publishedBlock,
        out CostBlock? harnessBlock)
    {
        long? freshInput = TryFindTokenCount(record.Tokens, TokenClass.Input);
        long? cacheRead = TryFindTokenCount(record.Tokens, TokenClass.CacheRead);
        long? cacheCreation = TryFindTokenCount(record.Tokens, TokenClass.CacheWrite);
        long? output = TryFindTokenCount(record.Tokens, TokenClass.Output);
        long? reasoning = TryFindTokenCount(record.Tokens, TokenClass.Reasoning);

        long? inputTokens = freshInput.HasValue || cacheRead.HasValue || cacheCreation.HasValue
            ? (freshInput ?? 0L) + (cacheRead ?? 0L) + (cacheCreation ?? 0L)
            : null;

        long? durationMs = ComputeRecordDurationMs(record);

        ReadTimeCostSummary? reportedCost = null;
        harnessBlock = null;
        if (record.ReportedCost is not null)
        {
            IReadOnlyDictionary<string, double>? byModel = !string.IsNullOrEmpty(record.Model)
                ? new Dictionary<string, double>(StringComparer.Ordinal) { [record.Model] = record.ReportedCost.Amount }
                : null;
            harnessBlock = new CostBlock(
                CostBasis.Harness,
                CostStatus.Priced,
                record.ReportedCost.Amount,
                record.ReportedCost.Currency,
                byModel);
            reportedCost = ToReadTimeCostSummary(harnessBlock);
        }

        publishedBlock = PriceReportedTokens(freshInput, cacheRead, cacheCreation, output, reasoning, record, harnessId, pricing);

        return new ReadTimeTurnSummary(
            Position: position,
            SpanId: record.RecordId,
            TimestampUtc: FormatTimestamp(record.StartedAt),
            Model: record.Model,
            InputTokens: inputTokens,
            OutputTokens: output,
            FreshInputTokens: freshInput,
            CacheReadTokens: cacheRead,
            CacheCreationTokens: cacheCreation,
            ReasoningTokens: reasoning,
            DurationMs: durationMs,
            ReportedCost: reportedCost,
            PublishedCost: ToReadTimeCostSummary(publishedBlock));
    }

    /// <summary>
    /// Prices a turn from the classes and reported totals that were stored.
    /// Calls <see cref="CanonPricing.PriceStoredTokens"/>, the same rule as
    /// <c>SqliteRelationalStore.PriceReportedTokens</c>.
    /// </summary>
    private static CostBlock PriceReportedTokens(
        long? freshInput,
        long? cacheRead,
        long? cacheCreation,
        long? output,
        long? reasoning,
        CollectedRecord record,
        string harnessId,
        IModelPricingLookup? pricing)
    {
        return CanonPricing.PriceStoredTokens(
            freshInput,
            cacheRead,
            cacheCreation,
            output,
            reasoning,
            record.ReportedInputTokens,
            record.ReportedOutputTokens,
            record.Model,
            harnessId,
            pricing);
    }

    private static long? TryFindTokenCount(IReadOnlyList<CollectedTokenCount> tokens, TokenClass tokenClass)
    {
        foreach (CollectedTokenCount entry in tokens)
        {
            if (entry.TokenClass == tokenClass)
            {
                return entry.Count;
            }
        }

        return null;
    }

    private static long? ComputeRecordDurationMs(CollectedRecord record)
    {
        if (record.EndedAt.HasValue)
        {
            return Math.Max(0L, (long)Math.Round((record.EndedAt.Value - record.StartedAt).TotalMilliseconds));
        }

        return record.ReportedDurationMs;
    }

    private static ReadTimeCostSummary ToReadTimeCostSummary(CostBlock block)
    {
        List<ReadTimeModelCost> byModel = block.ByModel is null
            ? []
            :
            [
                .. block.ByModel
                    .OrderBy(static kv => kv.Key, StringComparer.Ordinal)
                    .Select(static kv => new ReadTimeModelCost(kv.Key, kv.Value)),
            ];

        return new ReadTimeCostSummary(block.Basis, block.Status, block.Value, block.Currency, byModel);
    }

    private static ReadTimeCostSummary AggregateCostBlocks(
        IReadOnlyList<CostBlock> blocks,
        CostBasis fallbackBasis,
        List<Problem> problems)
    {
        CostTotalResult result = CanonPricing.SumCosts(blocks);
        if (result.Ok && result.Total is not null)
        {
            return ToReadTimeCostSummary(result.Total);
        }

        if (result.Problem is not null)
        {
            problems.Add(result.Problem);
        }

        return new ReadTimeCostSummary(fallbackBasis, CostStatus.Partial, null, null, []);
    }

    private static List<ReadTimeSessionToolSummary> BuildSessionToolSummaries(
        StoredSessionProjection? projection,
        int turnCount,
        IReadOnlyDictionary<string, (long SchemaTokens, string? Server, HashSet<string> SpanIds)> defStats,
        IReadOnlyList<string> toolsInvoked,
        IReadOnlyDictionary<string, int> invCounts,
        IReadOnlyDictionary<string, int> errCounts)
    {
        List<ReadTimeSessionToolSummary> result = [];
        HashSet<string> seenNames = new(StringComparer.Ordinal);

        if (projection?.Schema is not null)
        {
            foreach (StoredSchemaRankedTool ranked in projection.Schema.RankedTools)
            {
                _ = seenNames.Add(ranked.ToolName);
                bool hasDef = defStats.TryGetValue(ranked.ToolName, out (long SchemaTokens, string? Server, HashSet<string> SpanIds) def);
                long schemaTokens = hasDef ? def.SchemaTokens : 0L;
                string? server = ranked.ServerName ?? (hasDef ? def.Server : null);
                int invocations = invCounts.GetValueOrDefault(ranked.ToolName);
                int errors = errCounts.GetValueOrDefault(ranked.ToolName);
                int turnsResident = hasDef && schemaTokens > 0 && ranked.CostTokens >= schemaTokens
                    ? (int)Math.Min(int.MaxValue, Math.Max(def.SpanIds.Count, ranked.CostTokens / schemaTokens))
                    : (turnCount > 0 ? turnCount : (hasDef ? def.SpanIds.Count : 0));
                result.Add(new ReadTimeSessionToolSummary(
                    Name: ranked.ToolName,
                    Server: server,
                    SchemaTokens: schemaTokens,
                    Invocations: invocations,
                    TurnsResident: turnsResident,
                    Errors: errors,
                    TotalSchemaCost: ranked.CostTokens,
                    Invoked: ranked.Invoked || invocations > 0));
            }
        }

        List<ReadTimeSessionToolSummary> remainingDefinitions = [];
        foreach (KeyValuePair<string, (long SchemaTokens, string? Server, HashSet<string> SpanIds)> kv in defStats)
        {
            if (!seenNames.Add(kv.Key))
            {
                continue;
            }

            int turnsResident = turnCount > 0 ? turnCount : kv.Value.SpanIds.Count;
            long totalSchemaCost = kv.Value.SchemaTokens * turnsResident;
            int invocations = invCounts.GetValueOrDefault(kv.Key);
            int errors = errCounts.GetValueOrDefault(kv.Key);
            remainingDefinitions.Add(new ReadTimeSessionToolSummary(
                Name: kv.Key,
                Server: kv.Value.Server,
                SchemaTokens: kv.Value.SchemaTokens,
                Invocations: invocations,
                TurnsResident: turnsResident,
                Errors: errors,
                TotalSchemaCost: totalSchemaCost,
                Invoked: invocations > 0));
        }

        remainingDefinitions.Sort(static (left, right) =>
        {
            int cmp = right.TotalSchemaCost.CompareTo(left.TotalSchemaCost);
            return cmp != 0 ? cmp : string.CompareOrdinal(left.Name, right.Name);
        });
        result.AddRange(remainingDefinitions);

        foreach (string invokedName in toolsInvoked)
        {
            if (!seenNames.Add(invokedName))
            {
                continue;
            }

            result.Add(new ReadTimeSessionToolSummary(
                Name: invokedName,
                Server: null,
                SchemaTokens: 0L,
                Invocations: invCounts.GetValueOrDefault(invokedName),
                TurnsResident: 0,
                Errors: errCounts.GetValueOrDefault(invokedName),
                TotalSchemaCost: 0L,
                Invoked: true));
        }

        return result;
    }

    private static List<ReadTimeSessionServerSummary> BuildSessionServerSummaries(
        StoredSessionProjection? projection,
        IReadOnlyList<ReadTimeSessionToolSummary> tools)
    {
        List<string> orderedServers = [];
        HashSet<string> seenServers = new(StringComparer.Ordinal);

        if (projection?.Schema is not null)
        {
            foreach (StoredSchemaServerTokens server in projection.Schema.Servers)
            {
                if (seenServers.Add(server.ServerName))
                {
                    orderedServers.Add(server.ServerName);
                }
            }
        }

        Dictionary<string, long> remainingByCost = new(StringComparer.Ordinal);
        foreach (ReadTimeSessionToolSummary tool in tools)
        {
            if (tool.Server is null || seenServers.Contains(tool.Server))
            {
                continue;
            }

            remainingByCost[tool.Server] = remainingByCost.GetValueOrDefault(tool.Server) + tool.TotalSchemaCost;
        }

        foreach (KeyValuePair<string, long> pair in remainingByCost
            .OrderByDescending(static kv => kv.Value)
            .ThenBy(static kv => kv.Key, StringComparer.Ordinal))
        {
            if (seenServers.Add(pair.Key))
            {
                orderedServers.Add(pair.Key);
            }
        }

        List<ReadTimeSessionServerSummary> summaries = new(orderedServers.Count);
        foreach (string server in orderedServers)
        {
            int toolCount = 0;
            long schemaTokens = 0;
            int invocations = 0;
            int unusedTools = 0;
            long unusedCost = 0;

            foreach (ReadTimeSessionToolSummary tool in tools)
            {
                if (!string.Equals(tool.Server, server, StringComparison.Ordinal))
                {
                    continue;
                }

                toolCount++;
                schemaTokens += tool.SchemaTokens;
                invocations += tool.Invocations;
                if (!tool.Invoked)
                {
                    unusedTools++;
                    unusedCost += tool.TotalSchemaCost;
                }
            }

            summaries.Add(new ReadTimeSessionServerSummary(
                Server: server,
                IsMcp: true,
                Tools: toolCount,
                SchemaTokens: schemaTokens,
                Invocations: invocations,
                UnusedTools: unusedTools,
                UnusedCost: unusedCost));
        }

        return summaries;
    }

    private static List<ReadTimeSessionRequestSummary> BuildRequestSummaries(
        IReadOnlyList<string> requestSpanIds,
        IReadOnlyList<CollectedRecord> records,
        IReadOnlyDictionary<string, CollectedRecord> recordsBySpanId,
        IReadOnlyList<(int Position, CollectedRecord Record)> turnEntries)
    {
        List<ReadTimeSessionRequestSummary> summaries = new(requestSpanIds.Count);
        foreach (string requestSpanId in requestSpanIds)
        {
            HashSet<string> subtree = CollectDescendantSpanIds(requestSpanId, records);
            List<CollectedRecord> ownedTurns = [];
            foreach ((int _, CollectedRecord turnRecord) in turnEntries)
            {
                if (subtree.Contains(turnRecord.RecordId))
                {
                    ownedTurns.Add(turnRecord);
                }
            }

            CollectedRecord? requestRecord = recordsBySpanId.GetValueOrDefault(requestSpanId);
            string? model = requestRecord?.Model;
            if (string.IsNullOrEmpty(model))
            {
                foreach (CollectedRecord ownedTurn in ownedTurns)
                {
                    if (!string.IsNullOrEmpty(ownedTurn.Model))
                    {
                        model = ownedTurn.Model;
                        break;
                    }
                }
            }

            string timestampUtc = requestRecord is not null
                ? FormatTimestamp(requestRecord.StartedAt)
                : (ownedTurns.Count > 0 ? FormatTimestamp(ownedTurns[0].StartedAt) : string.Empty);

            string? promptPreview = ExtractPromptPreview(requestRecord, ownedTurns);
            summaries.Add(new ReadTimeSessionRequestSummary(
                RequestSpanId: requestSpanId,
                TimestampUtc: timestampUtc,
                Turns: ownedTurns.Count,
                Model: model,
                PromptPreview: promptPreview));
        }

        return summaries;
    }

    private static HashSet<string> CollectDescendantSpanIds(string rootSpanId, IReadOnlyList<CollectedRecord> records)
    {
        Dictionary<string, List<string>> childrenByParent = new(StringComparer.Ordinal);
        foreach (CollectedRecord record in records)
        {
            if (string.IsNullOrEmpty(record.ParentRecordId))
            {
                continue;
            }

            if (!childrenByParent.TryGetValue(record.ParentRecordId, out List<string>? children))
            {
                children = [];
                childrenByParent[record.ParentRecordId] = children;
            }

            children.Add(record.RecordId);
        }

        HashSet<string> visited = new(StringComparer.Ordinal) { rootSpanId };
        Queue<string> pending = new();
        pending.Enqueue(rootSpanId);

        while (pending.Count > 0)
        {
            string current = pending.Dequeue();
            if (!childrenByParent.TryGetValue(current, out List<string>? children))
            {
                continue;
            }

            foreach (string child in children)
            {
                if (visited.Add(child))
                {
                    pending.Enqueue(child);
                }
            }
        }

        return visited;
    }

    private static string? ExtractPromptPreview(
        CollectedRecord? requestRecord,
        IReadOnlyList<CollectedRecord> ownedTurns)
    {
        if (requestRecord is not null && HasPromptBucketParts(requestRecord.Parts))
        {
            return ExtractPromptPreviewFromParts(requestRecord.Parts);
        }

        foreach (CollectedRecord ownedTurn in ownedTurns)
        {
            if (HasPromptBucketParts(ownedTurn.Parts))
            {
                return ExtractPromptPreviewFromParts(ownedTurn.Parts);
            }
        }

        return null;
    }

    private static bool HasPromptBucketParts(IReadOnlyList<CollectedContentPart> parts)
    {
        foreach (CollectedContentPart part in parts)
        {
            if (part.Bucket is ContentBucket.UserPrompt or ContentBucket.UserMessage or ContentBucket.ConversationHistory)
            {
                return true;
            }
        }

        return false;
    }

    private static string? ExtractPromptPreviewFromParts(IReadOnlyList<CollectedContentPart> parts)
    {
        ContentBucket[] priorityBuckets =
        [
            ContentBucket.UserPrompt,
            ContentBucket.UserMessage,
            ContentBucket.ConversationHistory,
        ];

        foreach (ContentBucket targetBucket in priorityBuckets)
        {
            foreach (CollectedContentPart part in parts)
            {
                if (part.Bucket != targetBucket)
                {
                    continue;
                }

                if (part.PlainText is null)
                {
                    return null;
                }

                string trimmed = part.PlainText.Trim();
                if (trimmed.Length == 0)
                {
                    continue;
                }

                return TruncateSurrogateSafe(trimmed, 200);
            }
        }

        return null;
    }

    private static string TruncateSurrogateSafe(string text, int maxLength)
    {
        if (text.Length <= maxLength)
        {
            return text;
        }

        int cut = maxLength;
        if (cut > 0 && char.IsHighSurrogate(text[cut - 1]))
        {
            cut--;
        }

        return text[..cut];
    }

    private static List<Problem> CollectSessionProblems(
        StoreState currentState,
        string sessionKey,
        HashSet<string> sessionSpanIds)
    {
        HashSet<long> problemIds = [];

        TableMetadata probSessMeta = GetTableMetadata("ingest_problem_session");
        foreach (object?[] row in currentState.GetRows("ingest_problem_session"))
        {
            if (string.Equals(GetString(probSessMeta, row, "session_id"), sessionKey, StringComparison.Ordinal))
            {
                _ = problemIds.Add(GetInt64(probSessMeta, row, "problem_id"));
            }
        }

        TableMetadata probSpanMeta = GetTableMetadata("ingest_problem_span");
        foreach (object?[] row in currentState.GetRows("ingest_problem_span"))
        {
            if (sessionSpanIds.Contains(GetString(probSpanMeta, row, "span_id")))
            {
                _ = problemIds.Add(GetInt64(probSpanMeta, row, "problem_id"));
            }
        }

        if (problemIds.Count == 0)
        {
            return [];
        }

        TableMetadata probMeta = GetTableMetadata("ingest_problem");
        List<Problem> problems = [];
        foreach (object?[] row in currentState.GetRows("ingest_problem")
            .OrderBy(r => GetInt64(probMeta, r, "problem_id")))
        {
            long problemId = GetInt64(probMeta, row, "problem_id");
            if (!problemIds.Contains(problemId))
            {
                continue;
            }

            string severityRaw = GetString(probMeta, row, "severity");
            ProblemSeverity severity = ParseAggregateProblemSeverity(severityRaw);
            string code = GetString(probMeta, row, "code");
            string message = GetString(probMeta, row, "message");
            string? location = ReadOptionalSingleString(currentState, "ingest_problem_location", "problem_id", problemId, "location")
                ?? ReadOptionalSingleString(currentState, "ingest_problem_span", "problem_id", problemId, "span_id");
            string? stack = ReadOptionalSingleString(currentState, "ingest_problem_stack", "problem_id", problemId, "stack");

            problems.Add(new Problem(severity, code, message, location, stack));
        }

        return problems;
    }

    private static ProblemSeverity ParseAggregateProblemSeverity(string value)
    {
        if (string.Equals(value, "error", StringComparison.OrdinalIgnoreCase))
        {
            return ProblemSeverity.Error;
        }

        if (string.Equals(value, "warning", StringComparison.OrdinalIgnoreCase))
        {
            return ProblemSeverity.Warning;
        }

        throw new InvalidOperationException($"Unrecognized ingest_problem.severity value '{value}'.");
    }

    private static List<SubagentSession> BuildSubagentSummaries(
        StoredSessionProjection? projection,
        IReadOnlyList<CollectedRecord> records,
        IReadOnlyDictionary<string, CollectedRecord> recordsBySpanId)
    {
        if (projection is not null)
        {
            if (projection.TimelineSubagentSpanIds is null || projection.TimelineSubagentSpanIds.Count == 0)
            {
                return [];
            }

            List<SubagentSession> projected = new(projection.TimelineSubagentSpanIds.Count);
            foreach (string spanId in projection.TimelineSubagentSpanIds)
            {
                if (recordsBySpanId.TryGetValue(spanId, out CollectedRecord? record))
                {
                    projected.Add(new SubagentSession(
                        SpanId: record.RecordId,
                        ParentSpanId: record.Agent?.SpawningCallId ?? record.ParentRecordId,
                        Name: record.Agent?.AgentName ?? record.Name));
                }
            }

            return projected;
        }

        return
        [
            .. records
                .Where(static r => r.Op == RecordOp.AgentInvoke)
                .OrderBy(static r => r.StartedAt)
                .ThenBy(static r => r.RecordId, StringComparer.Ordinal)
                .Select(static r => new SubagentSession(
                    SpanId: r.RecordId,
                    ParentSpanId: r.Agent?.SpawningCallId ?? r.ParentRecordId,
                    Name: r.Agent?.AgentName ?? r.Name)),
        ];
    }

    private static ReadTimeAuxiliarySummary BuildAuxiliarySummary(
        StoredSessionProjection? projection,
        IReadOnlyList<CollectedRecord> records,
        IReadOnlyDictionary<string, CollectedRecord> recordsBySpanId,
        string harnessId,
        IModelPricingLookup? pricing,
        List<Problem> problems)
    {
        List<string> spanIds;
        if (projection is not null)
        {
            spanIds = projection.TimelineAuxiliarySpanIds is { Count: > 0 }
                ? [.. projection.TimelineAuxiliarySpanIds]
                : [];
        }
        else
        {
            spanIds =
            [
                .. records
                    .Where(static r => IsAuxiliaryRecord(r))
                    .OrderBy(static r => r.StartedAt)
                    .ThenBy(static r => r.RecordId, StringComparer.Ordinal)
                    .Select(static r => r.RecordId),
            ];
        }

        List<CostBlock> reportedBlocks = [];
        List<CostBlock> publishedBlocks = [];
        foreach (string spanId in spanIds)
        {
            if (!recordsBySpanId.TryGetValue(spanId, out CollectedRecord? record))
            {
                continue;
            }

            if (record.ReportedCost is not null)
            {
                IReadOnlyDictionary<string, double>? byModel = !string.IsNullOrEmpty(record.Model)
                    ? new Dictionary<string, double>(StringComparer.Ordinal) { [record.Model] = record.ReportedCost.Amount }
                    : null;
                reportedBlocks.Add(new CostBlock(
                    CostBasis.Harness,
                    CostStatus.Priced,
                    record.ReportedCost.Amount,
                    record.ReportedCost.Currency,
                    byModel));
            }

            long? freshInput = TryFindTokenCount(record.Tokens, TokenClass.Input);
            long? cacheRead = TryFindTokenCount(record.Tokens, TokenClass.CacheRead);
            long? cacheCreation = TryFindTokenCount(record.Tokens, TokenClass.CacheWrite);
            long? output = TryFindTokenCount(record.Tokens, TokenClass.Output);
            long? reasoning = TryFindTokenCount(record.Tokens, TokenClass.Reasoning);
            publishedBlocks.Add(PriceReportedTokens(freshInput, cacheRead, cacheCreation, output, reasoning, record, harnessId, pricing));
        }

        ReadTimeCostSummary? reportedCost = reportedBlocks.Count > 0
            ? AggregateCostBlocks(reportedBlocks, CostBasis.Harness, problems)
            : null;
        ReadTimeCostSummary publishedCost = publishedBlocks.Count > 0
            ? AggregateCostBlocks(publishedBlocks, CostBasis.Published, problems)
            : new ReadTimeCostSummary(CostBasis.Unknown, CostStatus.NoRate, null, null, []);

        return new ReadTimeAuxiliarySummary(spanIds, reportedCost, publishedCost);
    }

    private static bool IsAuxiliaryRecord(CollectedRecord record)
    {
        string trimmed = record.Name.Trim();
        if (string.Equals(trimmed, "title", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Contains("xtabprovider", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        string? agentName = record.Agent?.AgentName;
        return !string.IsNullOrEmpty(agentName) &&
               (string.Equals(agentName.Trim(), "title", StringComparison.OrdinalIgnoreCase) ||
                agentName.Contains("xtabprovider", StringComparison.OrdinalIgnoreCase));
    }

    private static ReadTimeContextBucketSummary BuildContextBucketSummary(
        StoredSessionContextTurn contextTurn,
        IReadOnlyList<(int Position, CollectedRecord Record)> turnEntries)
    {
        long? reportedInput = null;
        foreach ((int position, CollectedRecord record) in turnEntries)
        {
            if (position == contextTurn.Position)
            {
                reportedInput = record.ReportedInputTokens;
                break;
            }
        }

        List<ReadTimeContextBucketTokens> buckets =
        [
            .. contextTurn.Buckets.Select(static b => new ReadTimeContextBucketTokens(b.Bucket, b.Tokens)),
        ];

        return new ReadTimeContextBucketSummary(buckets, reportedInput);
    }

    private static UnusedSchemaRange? ResolveUnusedRange(
        StoredSessionProjection? projection,
        string harnessId,
        IReadOnlyList<string> models,
        IModelPricingLookup? pricing)
    {
        if (projection?.Schema?.UnusedTokenResidencies is not long residencies)
        {
            return null;
        }

        string? primaryModel = models.Count > 0 ? models[0] : null;
        if (!string.IsNullOrEmpty(primaryModel))
        {
            if (CanonPricing.IsCopilotHarness(harnessId))
            {
                if (CanonPricing.CopilotCreditsTable.PublishedRates is not null &&
                    CanonPricing.CopilotCreditsTable.PublishedRates.TryGetValue(primaryModel, out TableRate? tableRate) &&
                    tableRate.Billed)
                {
                    RateTier? firstTier = tableRate.Tiers is { Count: > 0 } ? tableRate.Tiers[0] : null;
                    double freshRate = firstTier?.InputRate ?? tableRate.InputRate;
                    double cacheReadRate = (firstTier?.CacheReadRate ?? tableRate.CacheReadRate) ?? freshRate;
                    if (freshRate > 0 || cacheReadRate > 0)
                    {
                        double cacheBound = (residencies * cacheReadRate) / CanonPricing.TokensPerMillion;
                        double freshBound = (residencies * freshRate) / CanonPricing.TokensPerMillion;
                        return new UnusedSchemaRange(
                            TokenResidencies: residencies,
                            Floor: Math.Min(cacheBound, freshBound),
                            Ceiling: Math.Max(cacheBound, freshBound),
                            Currency: CanonPricing.CopilotCreditsTable.Currency ?? "USD");
                    }
                }
            }
            else if (CanonPricing.IsPublishedTableHarness(harnessId) && !CanonPricing.IsFlatRateModel(primaryModel))
            {
                ModelCosts? costs = CanonPricing.GetModelCosts(primaryModel, pricing);
                if (costs is not null && (costs.InputCostPerToken > 0 || costs.CacheReadCostPerToken > 0))
                {
                    double cacheRate = costs.CacheReadCostPerToken > 0 ? costs.CacheReadCostPerToken : costs.InputCostPerToken;
                    double freshRate = costs.InputCostPerToken > 0 ? costs.InputCostPerToken : costs.CacheReadCostPerToken;
                    double cacheBound = residencies * cacheRate;
                    double freshBound = residencies * freshRate;
                    return new UnusedSchemaRange(
                        TokenResidencies: residencies,
                        Floor: Math.Min(cacheBound, freshBound),
                        Ceiling: Math.Max(cacheBound, freshBound),
                        Currency: "USD");
                }
            }
        }

        return new UnusedSchemaRange(residencies, 0, residencies, null);
    }

    private static SessionScope ResolveSessionScope(StoreState currentState, string sessionKey)
    {
        HashSet<string> spanIds = ResolveSessionSpanIds(currentState, sessionKey);
        HashSet<string> sourceIds = new(StringComparer.Ordinal);
        HashSet<string> harnessIds = new(StringComparer.Ordinal);

        TableMetadata recordMeta = GetTableMetadata("record");
        foreach (object?[] row in currentState.GetRows("record"))
        {
            if (spanIds.Contains(GetString(recordMeta, row, "span_id")))
            {
                _ = sourceIds.Add(GetString(recordMeta, row, "source_id"));
                _ = harnessIds.Add(GetString(recordMeta, row, "harness_id"));
            }
        }

        object?[]? sessionRow = FindRowByKey(currentState, "session", [("session_id", sessionKey)]);
        if (sessionRow is not null)
        {
            _ = harnessIds.Add(GetString(GetTableMetadata("session"), sessionRow, "harness_id"));
        }

        HashSet<string> executionIds = new(StringComparer.Ordinal);
        TableMetadata execSessMeta = GetTableMetadata("execution_session");
        foreach (object?[] row in currentState.GetRows("execution_session"))
        {
            if (string.Equals(GetString(execSessMeta, row, "session_id"), sessionKey, StringComparison.Ordinal))
            {
                _ = executionIds.Add(GetString(execSessMeta, row, "execution_id"));
            }
        }

        HashSet<string> runIds = new(StringComparer.Ordinal);
        TableMetadata execMeta = GetTableMetadata("execution");
        foreach (object?[] row in currentState.GetRows("execution"))
        {
            if (executionIds.Contains(GetString(execMeta, row, "execution_id")))
            {
                _ = runIds.Add(GetString(execMeta, row, "run_id"));
            }
        }

        HashSet<string> findingIds = new(StringComparer.Ordinal);
        TableMetadata findSessMeta = GetTableMetadata("finding_session");
        foreach (object?[] row in currentState.GetRows("finding_session"))
        {
            if (string.Equals(GetString(findSessMeta, row, "session_id"), sessionKey, StringComparison.Ordinal))
            {
                _ = findingIds.Add(GetString(findSessMeta, row, "finding_id"));
            }
        }

        HashSet<string> predictionIds = new(StringComparer.Ordinal);
        TableMetadata predMeta = GetTableMetadata("prediction");
        foreach (object?[] row in currentState.GetRows("prediction"))
        {
            if (findingIds.Contains(GetString(predMeta, row, "finding_id")) ||
                runIds.Contains(GetString(predMeta, row, "run_id")))
            {
                _ = predictionIds.Add(GetString(predMeta, row, "prediction_id"));
            }
        }

        HashSet<long> problemIds = [];
        TableMetadata probSessMeta = GetTableMetadata("ingest_problem_session");
        foreach (object?[] row in currentState.GetRows("ingest_problem_session"))
        {
            if (string.Equals(GetString(probSessMeta, row, "session_id"), sessionKey, StringComparison.Ordinal))
            {
                _ = problemIds.Add(GetInt64(probSessMeta, row, "problem_id"));
            }
        }

        return new SessionScope(sessionKey, spanIds, sourceIds, harnessIds, executionIds, runIds, findingIds, predictionIds, problemIds);
    }

    private static List<object?[]> FilterRowsForSessionScope(StoreState currentState, TableDefinition table, SessionScope scope)
    {
        TableMetadata meta = GetTableMetadata(table.Name);
        List<object?[]> allRows = currentState.GetRows(table.Name);
        List<object?[]> matched = [];

        foreach (object?[] row in allRows)
        {
            if (RowBelongsToScope(table.Name, meta, row, scope))
            {
                matched.Add(row);
            }
        }

        return matched;
    }

    private static bool RowBelongsToScope(string tableName, TableMetadata meta, object?[] row, SessionScope scope)
    {
        if (string.Equals(tableName, "harness", StringComparison.Ordinal) ||
            tableName.StartsWith("harness_rollup", StringComparison.Ordinal))
        {
            return scope.HarnessIds.Contains(GetString(meta, row, "harness_id"));
        }

        if (string.Equals(tableName, "source", StringComparison.Ordinal))
        {
            return scope.SourceIds.Contains(GetString(meta, row, "source_id"));
        }

        if (string.Equals(tableName, "session", StringComparison.Ordinal) ||
            tableName.StartsWith("session_", StringComparison.Ordinal))
        {
            return meta.ColumnIndices.ContainsKey("session_id") &&
                   string.Equals(GetString(meta, row, "session_id"), scope.SessionKey, StringComparison.Ordinal);
        }

        if (tableName.StartsWith("run", StringComparison.Ordinal))
        {
            return scope.RunIds.Contains(GetString(meta, row, "run_id"));
        }

        if (tableName.StartsWith("execution", StringComparison.Ordinal))
        {
            return scope.ExecutionIds.Contains(GetString(meta, row, "execution_id"));
        }

        if (tableName.StartsWith("finding", StringComparison.Ordinal))
        {
            return scope.FindingIds.Contains(GetString(meta, row, "finding_id"));
        }

        if (tableName.StartsWith("prediction", StringComparison.Ordinal))
        {
            return scope.PredictionIds.Contains(GetString(meta, row, "prediction_id"));
        }

        if (tableName.StartsWith("ingest_problem", StringComparison.Ordinal))
        {
            return scope.ProblemIds.Contains(GetInt64(meta, row, "problem_id"));
        }

        if (string.Equals(tableName, "projection_dirty_session", StringComparison.Ordinal))
        {
            return string.Equals(GetString(meta, row, "session_id"), scope.SessionKey, StringComparison.Ordinal);
        }

        if (meta.ColumnIndices.ContainsKey("span_id"))
        {
            return scope.SpanIds.Contains(GetString(meta, row, "span_id"));
        }

        return false;
    }

    private static List<string> ComputeMissingParentKeys(
        TableDefinition table,
        List<object?[]> childRows,
        Dictionary<string, List<object?[]>> scopedRowsByTable)
    {
        if (table.ForeignKeys.Count != 1)
        {
            return [];
        }

        ForeignKeyDefinition fk = table.ForeignKeys[0];
        if (!fk.Columns.SequenceEqual(table.PrimaryKeyColumns, StringComparer.Ordinal))
        {
            return [];
        }

        if (!scopedRowsByTable.TryGetValue(fk.ReferencedTable, out List<object?[]>? parentRows) || parentRows.Count == 0)
        {
            return [];
        }

        TableMetadata childMeta = GetTableMetadata(table.Name);
        TableMetadata parentMeta = GetTableMetadata(fk.ReferencedTable);
        int[] childKeyIndices = ResolveIndices(childMeta, fk.Columns);
        int[] parentKeyIndices = ResolveIndices(parentMeta, fk.ReferencedColumns);

        HashSet<string> presentChildKeys = new(StringComparer.Ordinal);
        foreach (object?[] childRow in childRows)
        {
            _ = presentChildKeys.Add(FormatCompositeKey(childRow, childKeyIndices));
        }

        List<string> missing = [];
        foreach (object?[] parentRow in parentRows)
        {
            string key = FormatCompositeKey(parentRow, parentKeyIndices);
            if (!presentChildKeys.Contains(key))
            {
                missing.Add(key);
            }
        }

        return missing;
    }

    private static string FormatCompositeKey(object?[] row, int[] keyIndices)
    {
        if (keyIndices.Length == 1)
        {
            return Convert.ToString(row[keyIndices[0]], CultureInfo.InvariantCulture) ?? string.Empty;
        }

        string[] parts = new string[keyIndices.Length];
        for (int index = 0; index < keyIndices.Length; index++)
        {
            parts[index] = Convert.ToString(row[keyIndices[index]], CultureInfo.InvariantCulture) ?? string.Empty;
        }

        return string.Join(":", parts);
    }

    private static StoredFieldRow ToStoredFieldRow(TableDefinition table, object?[] row)
    {
        StoredFieldCell[] cells = new StoredFieldCell[table.Columns.Count];
        for (int index = 0; index < table.Columns.Count; index++)
        {
            ColumnDefinition col = table.Columns[index];
            object? value = row[index];
            cells[index] = col.Type switch
            {
                SqliteColumnType.SqliteText => new StoredFieldCell(col.Name, col.Type, TextValue: (string?)value),
                SqliteColumnType.SqliteInteger => new StoredFieldCell(col.Name, col.Type, IntegerValue: (long?)value),
                SqliteColumnType.SqliteReal => new StoredFieldCell(col.Name, col.Type, RealValue: (double?)value),
                _ => throw new InvalidOperationException($"Unsupported column type '{col.Type}'."),
            };
        }

        return new StoredFieldRow(cells);
    }

    private static void ReplaceSingleton(
        StoreState working,
        string tableName,
        params (string ColumnName, object? Value)[] values)
    {
        _ = DeleteRowsCore(working, tableName, [("singleton_id", 1L)]);
        (string ColumnName, object? Value)[] rowValues = new (string, object?)[values.Length + 1];
        rowValues[0] = ("singleton_id", 1L);
        for (int index = 0; index < values.Length; index++)
        {
            rowValues[index + 1] = values[index];
        }

        InsertRowCore(working, tableName, rowValues);
    }

    private static object?[]? FindSingletonRow(StoreState currentState, string tableName) =>
        FindRowByKey(currentState, tableName, [("singleton_id", 1L)]);

    private static bool ReadSingletonBool(StoreState currentState, string tableName, string columnName, bool defaultValue)
    {
        object?[]? row = FindSingletonRow(currentState, tableName);
        return row is null ? defaultValue : GetInt64(GetTableMetadata(tableName), row, columnName) == 1L;
    }

    private static long ReadSingletonInt64(StoreState currentState, string tableName, string columnName, long defaultValue)
    {
        object?[]? row = FindSingletonRow(currentState, tableName);
        return row is null ? defaultValue : GetInt64(GetTableMetadata(tableName), row, columnName);
    }

    private static string? ReadSingletonString(StoreState currentState, string tableName, string columnName)
    {
        object?[]? row = FindSingletonRow(currentState, tableName);
        return row is null ? null : GetString(GetTableMetadata(tableName), row, columnName);
    }

    private static string? ReadOptionalSingleString(
        StoreState currentState,
        string tableName,
        string keyCol,
        object keyVal,
        string valueCol)
    {
        object?[]? row = FindRowByKey(currentState, tableName, [(keyCol, keyVal)]);
        return row is null ? null : GetString(GetTableMetadata(tableName), row, valueCol);
    }

    private static long? ReadOptionalSingleInt64(
        StoreState currentState,
        string tableName,
        string keyCol,
        object keyVal,
        string valueCol)
    {
        object?[]? row = FindRowByKey(currentState, tableName, [(keyCol, keyVal)]);
        return row is null ? null : GetInt64(GetTableMetadata(tableName), row, valueCol);
    }

    private static double? ReadOptionalSingleDouble(
        StoreState currentState,
        string tableName,
        string keyCol,
        object keyVal,
        string valueCol)
    {
        object?[]? row = FindRowByKey(currentState, tableName, [(keyCol, keyVal)]);
        return row is null ? null : GetDouble(GetTableMetadata(tableName), row, valueCol);
    }

    private static HashSet<string>? ResolveFilteredSpanIds(StoreState currentState, string? spanId, string? sessionKey)
    {
        if (spanId is null && sessionKey is null)
        {
            return null;
        }

        if (sessionKey is null)
        {
            return new HashSet<string>(StringComparer.Ordinal) { spanId! };
        }

        HashSet<string> sessionSpans = ResolveSessionSpanIds(currentState, sessionKey);
        if (spanId is not null)
        {
            return sessionSpans.Contains(spanId)
                ? new HashSet<string>(StringComparer.Ordinal) { spanId }
                : [];
        }

        return sessionSpans;
    }

    private static HashSet<string> ResolveSessionSpanIds(StoreState currentState, string sessionKey)
    {
        TableMetadata linkMeta = GetTableMetadata("record_session_link");
        HashSet<string> spanIds = new(StringComparer.Ordinal);
        foreach (object?[] row in currentState.GetRows("record_session_link"))
        {
            if (string.Equals(GetString(linkMeta, row, "session_key"), sessionKey, StringComparison.Ordinal))
            {
                _ = spanIds.Add(GetString(linkMeta, row, "span_id"));
            }
        }

        return spanIds;
    }

    private static void CollectSpanIdsFromTable(StoreState currentState, string tableName, HashSet<string> target)
    {
        TableMetadata meta = GetTableMetadata(tableName);
        foreach (object?[] row in currentState.GetRows(tableName))
        {
            _ = target.Add(GetString(meta, row, "span_id"));
        }
    }

    private static string GetString(TableMetadata meta, object?[] row, string columnName) =>
        (string)row[meta.ColumnIndices[columnName]]!;

    private static long GetInt64(TableMetadata meta, object?[] row, string columnName) =>
        (long)row[meta.ColumnIndices[columnName]]!;

    private static double GetDouble(TableMetadata meta, object?[] row, string columnName) =>
        (double)row[meta.ColumnIndices[columnName]]!;

    private static string FormatTimestamp(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset ParseTimestamp(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    private static string FormatSourceKind(SourceKind kind) => kind switch
    {
        SourceKind.File => "file",
        SourceKind.Otlp => "otlp",
        _ => kind.ToString(),
    };

    private static SourceKind ParseSourceKind(string value) => value switch
    {
        "file" => SourceKind.File,
        "otlp" => SourceKind.Otlp,
        _ => throw new InvalidOperationException($"Unknown source_kind '{value}'."),
    };

    private static string FormatRecordOp(RecordOp op) => op switch
    {
        RecordOp.LlmInvoke => "llm.invoke",
        RecordOp.ToolInvoke => "tool.invoke",
        RecordOp.AgentInvoke => "agent.invoke",
        RecordOp.Unspecified => "unspecified",
        _ => op.ToString(),
    };

    private static RecordOp ParseRecordOp(string value) => value switch
    {
        "llm.invoke" => RecordOp.LlmInvoke,
        "tool.invoke" => RecordOp.ToolInvoke,
        "agent.invoke" => RecordOp.AgentInvoke,
        "unspecified" => RecordOp.Unspecified,
        _ => throw new InvalidOperationException($"Unknown record.op '{value}'."),
    };

    private static string FormatRecordKind(RecordKind kind) => kind switch
    {
        RecordKind.Unspecified => "unspecified",
        RecordKind.Internal => "internal",
        RecordKind.Server => "server",
        RecordKind.Client => "client",
        RecordKind.Producer => "producer",
        RecordKind.Consumer => "consumer",
        _ => kind.ToString(),
    };

    private static RecordKind ParseRecordKind(string value) => value switch
    {
        "unspecified" => RecordKind.Unspecified,
        "internal" => RecordKind.Internal,
        "server" => RecordKind.Server,
        "client" => RecordKind.Client,
        "producer" => RecordKind.Producer,
        "consumer" => RecordKind.Consumer,
        _ => throw new InvalidOperationException($"Unknown record.kind '{value}'."),
    };

    private static string FormatRecordStatus(RecordStatus status) => status switch
    {
        RecordStatus.Unset => "unset",
        RecordStatus.Ok => "ok",
        RecordStatus.Error => "error",
        RecordStatus.Unspecified => "unspecified",
        _ => status.ToString(),
    };

    private static RecordStatus ParseRecordStatus(string value) => value switch
    {
        "unset" => RecordStatus.Unset,
        "ok" => RecordStatus.Ok,
        "error" => RecordStatus.Error,
        "unspecified" => RecordStatus.Unspecified,
        _ => throw new InvalidOperationException($"Unknown record.status '{value}'."),
    };

    private static string FormatTokenClass(TokenClass tokenClass) => tokenClass switch
    {
        TokenClass.Input => "input",
        TokenClass.CacheRead => "cache_read",
        TokenClass.CacheWrite => "cache_write",
        TokenClass.Output => "output",
        TokenClass.Reasoning => "reasoning",
        _ => tokenClass.ToString(),
    };

    private static TokenClass ParseTokenClass(string value) => value switch
    {
        "input" => TokenClass.Input,
        "cache_read" => TokenClass.CacheRead,
        "cache_write" => TokenClass.CacheWrite,
        "output" => TokenClass.Output,
        "reasoning" => TokenClass.Reasoning,
        _ => throw new InvalidOperationException($"Unknown token_class '{value}'."),
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
        _ => bucket.ToString(),
    };

    private static ContentBucket ParseContentBucket(string value) => value switch
    {
        "system_prompt" => ContentBucket.SystemPrompt,
        "tool_definitions" => ContentBucket.ToolDefinitions,
        "tool_definition" => ContentBucket.ToolDefinition,
        "instruction_context" => ContentBucket.InstructionContext,
        "conversation_history" => ContentBucket.ConversationHistory,
        "tool_result_content" => ContentBucket.ToolResultContent,
        "user_prompt" => ContentBucket.UserPrompt,
        "user_message" => ContentBucket.UserMessage,
        "reasoning" => ContentBucket.Reasoning,
        _ => throw new InvalidOperationException($"Unknown content_part.bucket '{value}'."),
    };

    private static TableMetadata GetTableMetadata(string tableName)
    {
        _ = RelationalSchemaCatalog.GetTable(tableName);
        return CatalogMetadata[tableName];
    }

    private static int[] ResolveIndices(TableMetadata meta, IReadOnlyList<string> columnNames)
    {
        int[] indices = new int[columnNames.Count];
        for (int index = 0; index < columnNames.Count; index++)
        {
            indices[index] = meta.ColumnIndices[columnNames[index]];
        }

        return indices;
    }

    private static Dictionary<string, TableMetadata> BuildCatalogMetadata()
    {
        Dictionary<string, TableMetadata> map = new(RelationalSchemaCatalog.Tables.Count, StringComparer.Ordinal);
        Dictionary<string, Dictionary<string, int>> columnIndicesByTable = new(RelationalSchemaCatalog.Tables.Count, StringComparer.Ordinal);

        foreach (TableDefinition table in RelationalSchemaCatalog.Tables)
        {
            Dictionary<string, int> colMap = new(table.Columns.Count, StringComparer.Ordinal);
            for (int index = 0; index < table.Columns.Count; index++)
            {
                colMap[table.Columns[index].Name] = index;
            }

            columnIndicesByTable[table.Name] = colMap;
        }

        Dictionary<string, List<ForeignKeyReference>> dependentsByParent = new(RelationalSchemaCatalog.Tables.Count, StringComparer.Ordinal);
        foreach (TableDefinition table in RelationalSchemaCatalog.Tables)
        {
            dependentsByParent[table.Name] = [];
        }

        foreach (TableDefinition table in RelationalSchemaCatalog.Tables)
        {
            Dictionary<string, int> colMap = columnIndicesByTable[table.Name];
            int[] pkIndices = [.. table.PrimaryKeyColumns.Select(name => colMap[name])];

            List<IndexMetadata> uniqueIndexes = [];
            foreach (IndexDefinition index in table.Indexes)
            {
                if (index.IsUnique)
                {
                    uniqueIndexes.Add(new IndexMetadata(
                        index.Name,
                        [.. index.Columns.Select(col => colMap[col.Name])]));
                }
            }

            List<ForeignKeyMetadata> foreignKeys = [];
            foreach (ForeignKeyDefinition fk in table.ForeignKeys)
            {
                Dictionary<string, int> parentColMap = columnIndicesByTable[fk.ReferencedTable];
                foreignKeys.Add(new ForeignKeyMetadata(
                    fk.ReferencedTable,
                    [.. fk.Columns.Select(name => colMap[name])],
                    [.. fk.ReferencedColumns.Select(name => parentColMap[name])]));
            }

            map[table.Name] = new TableMetadata(
                table,
                colMap,
                pkIndices,
                uniqueIndexes,
                foreignKeys,
                dependentsByParent[table.Name]);
        }

        foreach (TableMetadata meta in map.Values)
        {
            foreach (ForeignKeyMetadata fk in meta.ForeignKeys)
            {
                dependentsByParent[fk.ReferencedTable].Add(new ForeignKeyReference(meta, fk));
            }
        }

        return map;
    }

    private sealed class StoreState
    {
        private readonly Dictionary<string, List<object?[]>> tables;

        public long NextProblemId { get; set; } = 1;

        public long NextIngestId { get; set; } = 1;

        public long NextPointId { get; set; } = 1;

        private StoreState(Dictionary<string, List<object?[]>> tables) =>
            this.tables = tables;

        public static StoreState CreateEmpty()
        {
            Dictionary<string, List<object?[]>> empty = new(RelationalSchemaCatalog.Tables.Count, StringComparer.Ordinal);
            foreach (TableDefinition table in RelationalSchemaCatalog.Tables)
            {
                empty[table.Name] = [];
            }

            return new StoreState(empty);
        }

        public List<object?[]> GetRows(string tableName) => tables[tableName];

        public StoreState Clone()
        {
            Dictionary<string, List<object?[]>> cloned = new(tables.Count, StringComparer.Ordinal);
            foreach (KeyValuePair<string, List<object?[]>> entry in tables)
            {
                List<object?[]> copy = new(entry.Value.Count);
                foreach (object?[] row in entry.Value)
                {
                    object?[] rowCopy = new object?[row.Length];
                    Array.Copy(row, rowCopy, row.Length);
                    copy.Add(rowCopy);
                }

                cloned[entry.Key] = copy;
            }

            return new StoreState(cloned)
            {
                NextProblemId = NextProblemId,
                NextIngestId = NextIngestId,
                NextPointId = NextPointId,
            };
        }
    }

    private sealed record TableMetadata(
        TableDefinition Definition,
        Dictionary<string, int> ColumnIndices,
        int[] PrimaryKeyIndices,
        List<IndexMetadata> UniqueIndexes,
        List<ForeignKeyMetadata> ForeignKeys,
        List<ForeignKeyReference> DependentForeignKeys);

    private sealed record IndexMetadata(string Name, int[] ColumnIndices);

    private sealed record ForeignKeyMetadata(string ReferencedTable, int[] LocalIndices, int[] ReferencedIndices);

    private sealed record ForeignKeyReference(TableMetadata ChildMeta, ForeignKeyMetadata ForeignKey);

    private sealed record SessionScope(
        string SessionKey,
        HashSet<string> SpanIds,
        HashSet<string> SourceIds,
        HashSet<string> HarnessIds,
        HashSet<string> ExecutionIds,
        HashSet<string> RunIds,
        HashSet<string> FindingIds,
        HashSet<string> PredictionIds,
        HashSet<long> ProblemIds);
}
