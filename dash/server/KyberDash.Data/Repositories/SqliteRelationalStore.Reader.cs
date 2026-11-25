using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using KyberDash.Contracts.Repositories;
using KyberDash.Core.Exceptions;
using KyberDash.Data.Collector;
using KyberDash.Domain;
using KyberDash.Domain.Schema;

namespace KyberDash.Data.Repositories;

public partial class SqliteRelationalStore : IRelationalStoreReader
{
    private const string TabReadScriptHeader = ".headers off\n.mode tabs\n";

    /// <summary>
    /// The record's stored source (kind, harness, locator) as the last three columns of a record
    /// row; <see cref="ParseRecordSourceName"/> turns them into <see cref="CollectedRecord.SourceName"/>.
    /// </summary>
    private const string RecordSourceColumns =
        "hex(CAST(src.source_kind AS BLOB)), hex(CAST(src.harness_id AS BLOB)), hex(CAST(src.locator AS BLOB)) ";

    /// <summary>
    /// Joins a record (alias <c>r</c>) to its source row. An inner join, because
    /// <c>record.source_id</c> is a required foreign key: a record always has a source, and one
    /// whose source is deleted is cascaded away with it.
    /// </summary>
    private const string RecordSourceJoin = "JOIN source src ON src.source_id = r.source_id ";

    /// <summary>
    /// The record's response id and request id (<c>record_response_id</c>, <c>record_request_id</c>)
    /// as four typed-hex columns placed just before <see cref="RecordSourceColumns"/>, which stays
    /// last. They are the ids the projector joins an OTLP turn to its transcript twin on.
    /// </summary>
    private const string RecordJoinIdColumns =
        "typeof(rrid.response_id), COALESCE(hex(CAST(rrid.response_id AS BLOB)), ''), " +
        "typeof(rqid.request_id), COALESCE(hex(CAST(rqid.request_id AS BLOB)), ''), ";

    /// <summary>Left-joins a record (alias <c>r</c>) to its optional response-id and request-id rows.</summary>
    private const string RecordJoinIdJoin =
        "LEFT JOIN record_response_id rrid ON rrid.span_id = r.span_id " +
        "LEFT JOIN record_request_id rqid ON rqid.span_id = r.span_id ";

    /// <summary>
    /// The projection header: every <c>session_projection</c> column and its five single-row
    /// satellites, with no child collection. The full projection read and the paged header reads
    /// select the same fifteen columns, so both parse one row shape.
    /// </summary>
    private const string SessionProjectionHeaderColumns =
        "hex(CAST(sp.session_id AS BLOB)), hex(CAST(sp.label AS BLOB)), sp.is_subagent, sp.span_count, " +
        "hex(CAST(sp.started_at_utc AS BLOB)), hex(CAST(sp.ended_at_utc AS BLOB)), " +
        "typeof(spp.parent_session_id), COALESCE(hex(CAST(spp.parent_session_id AS BLOB)), ''), " +
        "typeof(spa.agent_name), COALESCE(hex(CAST(spa.agent_name AS BLOB)), ''), " +
        "CASE WHEN spip.session_id IS NOT NULL THEN 1 ELSE 0 END, " +
        "typeof(scu.reason), COALESCE(hex(CAST(scu.reason AS BLOB)), ''), " +
        "typeof(ssu.reason), COALESCE(hex(CAST(ssu.reason AS BLOB)), '')";

    /// <summary>The single-row satellites <see cref="SessionProjectionHeaderColumns"/> reads, joined on <c>sp.session_id</c>.</summary>
    private const string SessionProjectionHeaderJoins =
        "LEFT JOIN session_projection_parent spp ON spp.session_id = sp.session_id " +
        "LEFT JOIN session_projection_agent spa ON spa.session_id = sp.session_id " +
        "LEFT JOIN session_projection_included_in_parent spip ON spip.session_id = sp.session_id " +
        "LEFT JOIN session_context_unmeasurable scu ON scu.session_id = sp.session_id " +
        "LEFT JOIN session_schema_unmeasurable ssu ON ssu.session_id = sp.session_id";

    /// <summary>
    /// The sessions still standing after the root rule's last round: exactly the sessions on a
    /// parent chain that loops back to itself. No acyclic session outlives as many rounds as the
    /// store has projections.
    /// </summary>
    private const string CyclicSessionSet =
        "(SELECT session_id FROM keep WHERE round = (SELECT COUNT(*) FROM session_projection) + 1)";

    /// <inheritdoc />
    public IReadOnlyList<string> Harnesses()
    {
        string sql = TabReadScriptHeader +
                     "SELECT hex(CAST(harness_id AS BLOB)) FROM harness ORDER BY harness_id ASC;\n";

        string output = ExecuteSqlite(sql);
        List<string> results = [];
        foreach (string line in TabLines(output))
        {
            string[] cols = SplitTabLine(line, 1, "harness");
            results.Add(ParseHexRequired(cols[0], "harness.harness_id"));
        }

        return results;
    }

    /// <inheritdoc />
    public IReadOnlyList<StoredSource> Sources(string? harnessId = null)
    {
        StringBuilder sql = new(TabReadScriptHeader);
        sql.Append("SELECT hex(CAST(source_id AS BLOB)), hex(CAST(harness_id AS BLOB)), hex(CAST(source_kind AS BLOB)), hex(CAST(locator AS BLOB)) FROM source");
        if (harnessId is not null)
        {
            sql.Append(CultureInfo.InvariantCulture, $" WHERE harness_id = {TextExpr(harnessId)}");
        }

        sql.Append(" ORDER BY rowid ASC;\n");

        string output = ExecuteSqlite(sql.ToString());
        List<StoredSource> results = [];
        foreach (string line in TabLines(output))
        {
            string[] cols = SplitTabLine(line, 4, "source");
            string sourceId = ParseHexRequired(cols[0], "source.source_id");
            string rowHarness = ParseHexRequired(cols[1], "source.harness_id");
            SourceKind kind = ParseSourceKind(ParseHexRequired(cols[2], "source.source_kind"));
            string locator = ParseHexRequired(cols[3], "source.locator");
            results.Add(new StoredSource(sourceId, new CollectedSource(rowHarness, kind, locator)));
        }

        return results;
    }

    /// <inheritdoc />
    public int CountRecords(string? harnessId = null)
    {
        StringBuilder sql = new(TabReadScriptHeader);
        sql.Append("SELECT COUNT(*) FROM record");
        if (harnessId is not null)
        {
            sql.Append(CultureInfo.InvariantCulture, $" WHERE harness_id = {TextExpr(harnessId)}");
        }

        sql.Append(";\n");

        string output = ExecuteSqlite(sql.ToString());
        List<string> lines = [.. TabLines(output)];
        if (lines.Count != 1)
        {
            throw new StoredDataException($"Expected single scalar row from record count query, got {lines.Count}.");
        }

        string[] cols = SplitTabLine(lines[0], 1, "record count");
        return ParseInt32Required(cols[0], "record count");
    }

    /// <inheritdoc />
    public IReadOnlyList<StoredSourceCount> SourceRecordCounts()
    {
        string sql = TabReadScriptHeader +
                     "SELECT hex(CAST(source_id AS BLOB)), COUNT(*) FROM record " +
                     "WHERE source_id IS NOT NULL GROUP BY source_id ORDER BY source_id ASC;\n";

        List<StoredSourceCount> results = [];
        foreach (string line in TabLines(ExecuteSqlite(sql)))
        {
            string[] cols = SplitTabLine(line, 2, "record source count");
            results.Add(new StoredSourceCount(
                ParseHexRequired(cols[0], "record.source_id"),
                ParseInt32Required(cols[1], "record source count")));
        }

        return results;
    }

    /// <inheritdoc />
    public CollectedRecord? RecordContentFree(string spanId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(spanId);

        Dictionary<string, List<CollectedRecord>> recordsBySession =
            QueryContentFreeRecordsBySession([], includeToolMetadata: true, scopedSpanId: spanId);
        foreach (List<CollectedRecord> records in recordsBySession.Values)
        {
            foreach (CollectedRecord record in records)
            {
                if (string.Equals(record.RecordId, spanId, StringComparison.Ordinal))
                {
                    return record;
                }
            }
        }

        return null;
    }

    /// <inheritdoc />
    public string? SpanPromptText(string spanId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(spanId);

        // One span-scoped pass over the content-part tables in position order: the first
        // non-empty prompt bucket wins, the same parts the timeline's captured-prompt rule
        // chose before the stall fix moved the inspector off the session-wide read.
        StringBuilder sql = new(TabReadScriptHeader);
        sql.Append(
            "SELECT hex(CAST(cp.bucket AS BLOB)), " +
            "typeof(cpt.part_text), COALESCE(hex(CAST(cpt.part_text AS BLOB)), '') " +
            "FROM content_part cp " +
            "LEFT JOIN content_part_text cpt ON cpt.span_id = cp.span_id AND cpt.position = cp.position " +
            $"WHERE cp.span_id = {TextExpr(spanId)} ORDER BY cp.position ASC;\n");

        string output = ExecuteSqlite(sql.ToString());
        foreach (string line in TabLines(output))
        {
            string[] cols = SplitTabLine(line, 3, "span prompt");
            ContentBucket bucket = ParseContentBucket(ParseHexRequired(cols[0], "content_part.bucket"));
            if (bucket is not (ContentBucket.UserPrompt or ContentBucket.UserMessage or ContentBucket.ConversationHistory))
            {
                continue;
            }

            string? text = ParseNullableHex(cols[1], cols[2], "content_part_text.part_text");
            if (string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            return text;
        }

        return null;
    }

    /// <inheritdoc />
    public IReadOnlyList<StoredSessionAttribution> SessionAttributions()
    {
        string sql = TabReadScriptHeader +
                     "SELECT hex(CAST(rsl.session_key AS BLOB)), hex(CAST(r.harness_id AS BLOB)), COUNT(*) " +
                     "FROM record r JOIN record_session_link rsl ON rsl.span_id = r.span_id " +
                     "GROUP BY rsl.session_key, r.harness_id " +
                     "ORDER BY rsl.session_key ASC, r.harness_id ASC;\n";

        string output = ExecuteSqlite(sql);
        List<StoredSessionAttribution> results = [];
        foreach (string line in TabLines(output))
        {
            string[] cols = SplitTabLine(line, 3, "record attribution");
            results.Add(new StoredSessionAttribution(
                ParseHexRequired(cols[0], "record_session_link.session_key"),
                ParseHexRequired(cols[1], "record.harness_id"),
                ParseInt32Required(cols[2], "record attribution count")));
        }

        return results;
    }

    /// <inheritdoc />
    public IReadOnlyList<CollectedRecord> SessionRecordsForRebuild(string sessionKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionKey);
        return QueryRecords(RecordSessionLinkPredicate(sessionKey));
    }

    /// <inheritdoc />
    public IReadOnlyList<CollectedRecord> AllSessionRecordsForMaintenance() => QueryRecords(null);

    /// <summary>Builds the <c>record_session_link</c> existence predicate selecting a session key's records.</summary>
    /// <param name="sessionKey">Canonical session key.</param>
    private static string RecordSessionLinkPredicate(string sessionKey) =>
        $"EXISTS (SELECT 1 FROM record_session_link rsl WHERE rsl.span_id = r.span_id AND rsl.session_key = {TextExpr(sessionKey)})";

    /// <inheritdoc />
    public IReadOnlyList<StoredTokenCount> TokenCounts(string? spanId = null, string? sessionKey = null)
    {
        string whereClause = BuildSpanFilterWhere("rtc", spanId, sessionKey);
        string sql = TabReadScriptHeader +
                     "SELECT hex(CAST(rtc.span_id AS BLOB)), hex(CAST(rtc.token_class AS BLOB)), rtc.token_count " +
                     $"FROM record_token_count rtc{whereClause} ORDER BY rtc.rowid ASC;\n";

        string output = ExecuteSqlite(sql);
        List<StoredTokenCount> results = [];
        foreach (string line in TabLines(output))
        {
            string[] cols = SplitTabLine(line, 3, "record_token_count");
            string rowSpan = ParseHexRequired(cols[0], "record_token_count.span_id");
            TokenClass tokenClass = ParseTokenClass(ParseHexRequired(cols[1], "record_token_count.token_class"));
            long count = ParseInt64Required(cols[2], "record_token_count.token_count");
            results.Add(new StoredTokenCount(rowSpan, tokenClass, count));
        }

        return results;
    }

    /// <inheritdoc />
    public IReadOnlyList<StoredReportedTotal> ReportedTotals(string? spanId = null, string? sessionKey = null)
    {
        string whereClause = BuildSpanFilterWhere("rrt", spanId, sessionKey);
        string sql = TabReadScriptHeader +
                     "SELECT hex(CAST(rrt.span_id AS BLOB)), hex(CAST(rrt.direction AS BLOB)), rrt.token_count " +
                     $"FROM record_reported_total rrt{whereClause} ORDER BY rrt.rowid ASC;\n";

        string output = ExecuteSqlite(sql);
        List<StoredReportedTotal> results = [];
        foreach (string line in TabLines(output))
        {
            string[] cols = SplitTabLine(line, 3, "record_reported_total");
            string rowSpan = ParseHexRequired(cols[0], "record_reported_total.span_id");
            ReportedTotalDirection direction = ParseReportedDirection(ParseHexRequired(cols[1], "record_reported_total.direction"));
            long count = ParseInt64Required(cols[2], "record_reported_total.token_count");
            results.Add(new StoredReportedTotal(rowSpan, direction, count));
        }

        return results;
    }

    /// <inheritdoc />
    public IReadOnlyList<StoredReportedCost> ReportedCosts(string? spanId = null, string? sessionKey = null)
    {
        string whereClause = BuildSpanFilterWhere("rrc", spanId, sessionKey);
        string sql = TabReadScriptHeader +
                     "SELECT hex(CAST(rrc.span_id AS BLOB)), rrc.amount, hex(CAST(rrc.currency AS BLOB)) " +
                     $"FROM record_reported_cost rrc{whereClause} ORDER BY rrc.rowid ASC;\n";

        string output = ExecuteSqlite(sql);
        List<StoredReportedCost> results = [];
        foreach (string line in TabLines(output))
        {
            string[] cols = SplitTabLine(line, 3, "record_reported_cost");
            string rowSpan = ParseHexRequired(cols[0], "record_reported_cost.span_id");
            double amount = ParseDoubleRequired(cols[1], "record_reported_cost.amount");
            string currency = ParseHexRequired(cols[2], "record_reported_cost.currency");
            results.Add(new StoredReportedCost(rowSpan, amount, currency));
        }

        return results;
    }

    /// <inheritdoc />
    public IReadOnlyList<StoredSkillInvocation> SkillsInvoked(string? spanId = null, string? sessionKey = null)
    {
        string whereClause = BuildSpanFilterWhere("rsi", spanId, sessionKey);
        string sql = TabReadScriptHeader +
                     "SELECT hex(CAST(rsi.span_id AS BLOB)), hex(CAST(rsi.skill_name AS BLOB)) " +
                     $"FROM record_skill_invocation rsi{whereClause} ORDER BY rsi.rowid ASC;\n";

        string output = ExecuteSqlite(sql);
        List<StoredSkillInvocation> results = [];
        foreach (string line in TabLines(output))
        {
            string[] cols = SplitTabLine(line, 2, "record_skill_invocation");
            string rowSpan = ParseHexRequired(cols[0], "record_skill_invocation.span_id");
            string skillName = ParseHexRequired(cols[1], "record_skill_invocation.skill_name");
            results.Add(new StoredSkillInvocation(rowSpan, skillName));
        }

        return results;
    }

    /// <inheritdoc />
    public IReadOnlyList<StoredToolCall> ToolCalls(string? spanId = null, string? sessionKey = null)
    {
        string whereClause = BuildSpanFilterWhere("tc", spanId, sessionKey);
        string sql = TabReadScriptHeader +
                     BuildToolCallSelect("tc", whereClause, "ORDER BY tc.rowid ASC");

        string output = ExecuteSqlite(sql);
        List<StoredToolCall> results = [];
        foreach (string line in TabLines(output))
        {
            string[] cols = SplitTabLine(line, 33, "tool_call");
            string rowSpan = ParseHexRequired(cols[0], "tool_call.span_id");
            CollectedToolCall call = ParseToolCallColumns(cols, startIndex: 1);
            results.Add(new StoredToolCall(rowSpan, call));
        }

        return results;
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

        string? scopedSpan = spanId;
        if (string.IsNullOrEmpty(scopedSpan) && turnIndex.HasValue)
        {
            scopedSpan = ResolveTurnContentSpan(sessionKey, turnIndex.Value);
            if (scopedSpan is null)
            {
                return [];
            }
        }

        List<string> predicates =
        [
            $"EXISTS (SELECT 1 FROM record_session_link rsl WHERE rsl.span_id = cp.span_id AND rsl.session_key = {TextExpr(sessionKey)})",
        ];
        if (!string.IsNullOrEmpty(scopedSpan))
        {
            predicates.Add($"cp.span_id = {TextExpr(scopedSpan)}");
        }

        if (!string.IsNullOrEmpty(part))
        {
            predicates.Add($"cp.bucket = {TextExpr(part)}");
        }

        string whereClause = " WHERE " + string.Join(" AND ", predicates);
        bool cutInSql = !full && budget > 0;
        string sql = TabReadScriptHeader + (cutInSql
            ? BuildBudgetedContentPartSelect("cp", whereClause, budget)
            : BuildContentPartSelect("cp", whereClause, "ORDER BY cp.span_id ASC, cp.position ASC"));

        string output = ExecuteSqlite(sql);
        List<StoredContentPart> results = [];
        foreach (string line in TabLines(output))
        {
            string[] cols = SplitTabLine(line, cutInSql ? 22 : 21, "content_part");
            string rowSpan = ParseHexRequired(cols[0], "content_part.span_id");
            CollectedContentPart scopedPart = ParseContentPartColumns(cols, startIndex: 1);
            results.Add(new StoredContentPart(rowSpan, scopedPart));
        }

        return results;
    }

    /// <summary>
    /// Resolves a turn index to the span the turn figures answer for that index, so the content
    /// read is scoped to the same turn the summary already priced.
    /// </summary>
    /// <param name="sessionKey">Canonical session key.</param>
    /// <param name="turnIndex">Zero-based turn index.</param>
    /// <returns>The turn's span identifier, or <see langword="null"/> when the index is outside the session.</returns>
    private string? ResolveTurnContentSpan(string sessionKey, int turnIndex)
    {
        if (turnIndex < 0)
        {
            return null;
        }

        IReadOnlyList<(int Position, CollectedRecord Record)> turns = ResolveSessionTurnEntriesForReader(
            GetSessionTurns(sessionKey),
            SessionRecordsContentFree(sessionKey));
        return turnIndex < turns.Count ? turns[turnIndex].Record.RecordId : null;
    }

    /// <inheritdoc />
    public int TurnContentPartCount(
        string sessionKey,
        int? turnIndex = null,
        string? spanId = null,
        string? part = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionKey);

        string? scopedSpan = spanId;
        if (string.IsNullOrEmpty(scopedSpan) && turnIndex.HasValue)
        {
            scopedSpan = ResolveTurnContentSpan(sessionKey, turnIndex.Value);
            if (scopedSpan is null)
            {
                return 0;
            }
        }

        List<string> predicates =
        [
            $"EXISTS (SELECT 1 FROM record_session_link rsl WHERE rsl.span_id = cp.span_id AND rsl.session_key = {TextExpr(sessionKey)})",
        ];
        if (!string.IsNullOrEmpty(scopedSpan))
        {
            predicates.Add($"cp.span_id = {TextExpr(scopedSpan)}");
        }

        if (!string.IsNullOrEmpty(part))
        {
            predicates.Add($"cp.bucket = {TextExpr(part)}");
        }

        // COUNT over the scoped rows only: no content-text column is selected, so the count read
        // transfers no part text however large the parts are.
        string sql = TabReadScriptHeader +
                     $"SELECT COUNT(*) FROM content_part cp WHERE {string.Join(" AND ", predicates)};\n";

        string output = ExecuteSqlite(sql);
        foreach (string line in TabLines(output))
        {
            return ParseInt32Required(SplitTabLine(line, 1, "content_part.count")[0], "content_part.count");
        }

        return 0;
    }

    /// <inheritdoc />
    public IReadOnlyList<StoredToolDefinition> ToolDefinitions(string? spanId = null, string? sessionKey = null)
    {
        string whereClause = BuildSpanFilterWhere("td", spanId, sessionKey);
        string sql = TabReadScriptHeader +
                     "SELECT hex(CAST(td.span_id AS BLOB)), td.position, hex(CAST(td.tool_name AS BLOB)), " +
                     "typeof(tds.server_name), COALESCE(hex(CAST(tds.server_name AS BLOB)), ''), td.schema_tokens " +
                     "FROM tool_definition td " +
                     "LEFT JOIN tool_definition_server tds ON tds.span_id = td.span_id AND tds.position = td.position" +
                     $"{whereClause} ORDER BY td.span_id ASC, td.position ASC;\n";

        string output = ExecuteSqlite(sql);
        List<StoredToolDefinition> results = [];
        foreach (string line in TabLines(output))
        {
            string[] cols = SplitTabLine(line, 6, "tool_definition");
            string rowSpan = ParseHexRequired(cols[0], "tool_definition.span_id");
            int position = ParseInt32Required(cols[1], "tool_definition.position");
            string toolName = ParseHexRequired(cols[2], "tool_definition.tool_name");
            string? server = ParseNullableHex(cols[3], cols[4], "tool_definition_server.server_name");
            long schemaTokens = ParseInt64Required(cols[5], "tool_definition.schema_tokens");
            results.Add(new StoredToolDefinition(rowSpan, new CollectedToolDefinition(position, toolName, server, schemaTokens)));
        }

        return results;
    }

    /// <inheritdoc />
    public IReadOnlyList<StoredAgentInvocation> AgentInvocations(string? spanId = null, string? sessionKey = null)
    {
        string whereClause = BuildSpanFilterWhere("ai", spanId, sessionKey);
        string sql = TabReadScriptHeader +
                     BuildAgentInvocationSelect("ai", whereClause, "ORDER BY ai.rowid ASC");

        string output = ExecuteSqlite(sql);
        List<StoredAgentInvocation> results = [];
        foreach (string line in TabLines(output))
        {
            string[] cols = SplitTabLine(line, 8, "agent_invocation");
            string rowSpan = ParseHexRequired(cols[0], "agent_invocation.span_id");
            CollectedAgentInvocation invocation = ParseAgentInvocationColumns(cols, startIndex: 1);
            results.Add(new StoredAgentInvocation(rowSpan, invocation));
        }

        return results;
    }

    /// <inheritdoc />
    public IReadOnlyList<StoredRecordSignals> RecordSignals(string? spanId = null, string? sessionKey = null)
    {
        string filterClause = BuildSpanFilterWhere("sig", spanId, sessionKey);
        string sql = TabReadScriptHeader +
                     "WITH candidate_spans(span_id) AS (" +
                     "SELECT span_id FROM record_error UNION " +
                     "SELECT span_id FROM record_edit_failure UNION " +
                     "SELECT span_id FROM record_correction UNION " +
                     "SELECT span_id FROM record_declared_context_window" +
                     ") " +
                     "SELECT hex(CAST(sig.span_id AS BLOB)), " +
                     "typeof(re.message), COALESCE(hex(CAST(re.message AS BLOB)), ''), " +
                     "typeof(ref.failure_count), COALESCE(CAST(ref.failure_count AS TEXT), ''), " +
                     "typeof(rcr.correction_rule), COALESCE(hex(CAST(rcr.correction_rule AS BLOB)), ''), " +
                     "typeof(rdcw.declared_context_window), COALESCE(CAST(rdcw.declared_context_window AS TEXT), '') " +
                     "FROM candidate_spans sig " +
                     "LEFT JOIN record_error re ON re.span_id = sig.span_id " +
                     "LEFT JOIN record_edit_failure ref ON ref.span_id = sig.span_id " +
                     "LEFT JOIN record_correction_rule rcr ON rcr.span_id = sig.span_id " +
                     "LEFT JOIN record_declared_context_window rdcw ON rdcw.span_id = sig.span_id" +
                     $"{filterClause} ORDER BY sig.span_id ASC;\n";

        string output = ExecuteSqlite(sql);
        List<StoredRecordSignals> results = [];
        foreach (string line in TabLines(output))
        {
            string[] cols = SplitTabLine(line, 9, "record_signals");
            string rowSpan = ParseHexRequired(cols[0], "record_signals.span_id");
            string? errorMessage = ParseNullableHex(cols[1], cols[2], "record_error.message");
            long? editFailures = ParseNullableInt64(cols[3], cols[4], "record_edit_failure.failure_count");
            string? correctionRule = ParseNullableHex(cols[5], cols[6], "record_correction_rule.correction_rule");
            long? declaredWindow = ParseNullableInt64(cols[7], cols[8], "record_declared_context_window.declared_context_window");
            results.Add(new StoredRecordSignals(rowSpan, new CollectedRecordSignals(errorMessage, editFailures, correctionRule, declaredWindow)));
        }

        return results;
    }

    /// <inheritdoc />
    public CollectedSession? GetSession(string sessionKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionKey);
        IReadOnlyList<CollectedSession> sessions = QuerySessions($"s.session_id = {TextExpr(sessionKey)}", $"u.session_id = {TextExpr(sessionKey)}");
        return sessions.Count == 0 ? null : sessions[0];
    }

    /// <inheritdoc />
    public IReadOnlyList<CollectedSession> Sessions(string? harnessId = null)
    {
        if (harnessId is null)
        {
            return QuerySessions(sessionPredicate: null, usagePredicate: null);
        }

        string harnessExpr = TextExpr(harnessId);
        return QuerySessions(
            $"s.harness_id = {harnessExpr}",
            $"EXISTS (SELECT 1 FROM session s WHERE s.session_id = u.session_id AND s.harness_id = {harnessExpr})");
    }

    /// <inheritdoc />
    public IReadOnlyList<StoredSessionModelUsage> SessionModelUsages(string? sessionKey = null)
    {
        StringBuilder sql = new(TabReadScriptHeader);
        sql.Append("SELECT hex(CAST(session_id AS BLOB)), snapshot_position, hex(CAST(model_id AS BLOB)), hex(CAST(token_class AS BLOB)), token_count FROM session_model_usage");
        if (sessionKey is not null)
        {
            sql.Append(CultureInfo.InvariantCulture, $" WHERE session_id = {TextExpr(sessionKey)}");
        }

        sql.Append(" ORDER BY session_id ASC, snapshot_position ASC, model_id ASC, token_class ASC;\n");

        string output = ExecuteSqlite(sql.ToString());
        List<StoredSessionModelUsage> results = [];
        foreach (string line in TabLines(output))
        {
            string[] cols = SplitTabLine(line, 5, "session_model_usage");
            string rowSession = ParseHexRequired(cols[0], "session_model_usage.session_id");
            int position = ParseInt32Required(cols[1], "session_model_usage.snapshot_position");
            string modelId = ParseHexRequired(cols[2], "session_model_usage.model_id");
            TokenClass tokenClass = ParseTokenClass(ParseHexRequired(cols[3], "session_model_usage.token_class"));
            long count = ParseInt64Required(cols[4], "session_model_usage.token_count");
            results.Add(new StoredSessionModelUsage(rowSession, new CollectedModelUsage(position, modelId, tokenClass, count)));
        }

        return results;
    }

    /// <inheritdoc />
    public StoredFieldsInventoryView StoredFieldsInventory(string sessionKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionKey);

        string sessionExpr = TextExpr(sessionKey);
        IReadOnlyList<TableDefinition> tables = RelationalSchemaCatalog.Tables;

        // The inventory's missing-parent-key computation reads key columns alone: a qualifying
        // child's own key columns and the parent keys such children reference. Key columns are
        // identifiers, so the inventory never reads a row value (decision D15).
        Dictionary<string, List<ColumnDefinition>> keyColumnsByTable = StoredFieldsKeyColumns(tables);
        Dictionary<string, int[]> keyCellIndicesByTable = new(tables.Count, StringComparer.Ordinal);
        foreach (TableDefinition table in tables)
        {
            keyCellIndicesByTable[table.Name] =
                ResolveColumnIndices(table, [.. keyColumnsByTable[table.Name].Select(static c => c.Name)]);
        }

        StringBuilder sql = new(TabReadScriptHeader);
        for (int tableIndex = 0; tableIndex < tables.Count; tableIndex++)
        {
            TableDefinition table = tables[tableIndex];
            string? whereClause = BuildStoredFieldsScopeWhere(table, sessionExpr);
            if (whereClause is null)
            {
                continue;
            }

            sql.Append(CultureInfo.InvariantCulture, $"SELECT {tableIndex.ToString(CultureInfo.InvariantCulture)}, 'c', COUNT(*) FROM {table.Name} WHERE {whereClause};\n");
            List<ColumnDefinition> keyColumns = keyColumnsByTable[table.Name];
            if (keyColumns.Count == 0)
            {
                continue;
            }

            sql.Append(CultureInfo.InvariantCulture, $"SELECT {tableIndex.ToString(CultureInfo.InvariantCulture)}, 'k'");
            foreach (ColumnDefinition column in keyColumns)
            {
                AppendStoredFieldValueSelect(sql, table, column);
            }

            sql.Append(CultureInfo.InvariantCulture, $" FROM {table.Name} WHERE {whereClause} ORDER BY rowid ASC;\n");
        }

        string output = ExecuteSqlite(sql.ToString());
        long[] counts = new long[tables.Count];
        Dictionary<string, List<StoredFieldRow>> keyRowsByTable = new(tables.Count, StringComparer.Ordinal);
        foreach (TableDefinition table in tables)
        {
            keyRowsByTable[table.Name] = [];
        }

        foreach (string line in TabLines(output))
        {
            string[] cols = line.Split('\t');
            if (cols.Length < 3)
            {
                throw new StoredDataException("Empty row returned from the stored-fields inventory query.");
            }

            int tableIndex = ParseInt32Required(cols[0], "table_index");
            if (tableIndex < 0 || tableIndex >= tables.Count)
            {
                throw new StoredDataException($"Out-of-range table index '{tableIndex.ToString(CultureInfo.InvariantCulture)}' returned from the stored-fields inventory query.");
            }

            TableDefinition table = tables[tableIndex];
            switch (cols[1])
            {
                case "c":
                    if (cols.Length != 3)
                    {
                        throw new StoredDataException($"Expected 3 columns for the {table.Name} count, got {cols.Length.ToString(CultureInfo.InvariantCulture)}.");
                    }

                    counts[tableIndex] = ParseInt64Required(cols[2], $"{table.Name}.row_count");
                    break;
                case "k":
                    List<ColumnDefinition> keyColumns = keyColumnsByTable[table.Name];
                    if (cols.Length != 2 + (keyColumns.Count * 2))
                    {
                        throw new StoredDataException($"Expected {2 + (keyColumns.Count * 2)} columns for the {table.Name} key rows, got {cols.Length.ToString(CultureInfo.InvariantCulture)}.");
                    }

                    StoredFieldCell[] cells = new StoredFieldCell[table.Columns.Count];
                    for (int columnIndex = 0; columnIndex < table.Columns.Count; columnIndex++)
                    {
                        ColumnDefinition every = table.Columns[columnIndex];
                        cells[columnIndex] = new StoredFieldCell(every.Name, every.Type);
                    }

                    int[] keyCellIndices = keyCellIndicesByTable[table.Name];
                    for (int keyIndex = 0; keyIndex < keyColumns.Count; keyIndex++)
                    {
                        cells[keyCellIndices[keyIndex]] = ParseStoredFieldCell(
                            table.Name,
                            keyColumns[keyIndex],
                            cols[2 + (keyIndex * 2)],
                            cols[3 + (keyIndex * 2)]);
                    }

                    keyRowsByTable[table.Name].Add(new StoredFieldRow(cells));
                    break;
                default:
                    throw new StoredDataException($"Unexpected inventory row tag '{cols[1]}' for '{table.Name}'.");
            }
        }

        List<StoredTableInventory> tableEntries = new(tables.Count);
        for (int tableIndex = 0; tableIndex < tables.Count; tableIndex++)
        {
            TableDefinition table = tables[tableIndex];
            List<string> columnNames = [.. table.Columns.Select(static c => c.Name)];
            List<string> missingParentKeys = ComputeStoredFieldsMissingParentKeys(table, keyRowsByTable[table.Name], keyRowsByTable);
            tableEntries.Add(new StoredTableInventory(table.Name, columnNames, counts[tableIndex], missingParentKeys));
        }

        return new StoredFieldsInventoryView(sessionKey, tableEntries);
    }

    /// <inheritdoc />
    public StoredTableRows? StoredFieldsRows(string sessionKey, string table, int page, int limit)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(table);
        ArgumentOutOfRangeException.ThrowIfLessThan(page, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);

        // The route's table name is an allowlist key, never SQL: only a name the catalog lists
        // reaches the statement builder below.
        if (!RelationalSchemaCatalog.TryGetTable(table, out TableDefinition? definition) || definition is null)
        {
            return null;
        }

        List<string> columnNames = [.. definition.Columns.Select(static c => c.Name)];
        int offset = (int)Math.Clamp((long)(page - 1) * limit, 0L, int.MaxValue);
        string? whereClause = BuildStoredFieldsScopeWhere(definition, TextExpr(sessionKey));
        if (whereClause is null)
        {
            // A table the session scope cannot bind answers an empty page, as the inventory's
            // zero count does for the same table.
            return new StoredTableRows(definition.Name, columnNames, 0, page, limit, offset, []);
        }

        StringBuilder sql = new(TabReadScriptHeader);
        sql.Append(CultureInfo.InvariantCulture, $"SELECT 0, 'c', COUNT(*) FROM {definition.Name} WHERE {whereClause};\n");
        sql.Append("SELECT 0, 'k'");
        foreach (ColumnDefinition column in definition.Columns)
        {
            AppendStoredFieldValueSelect(sql, definition, column);
        }

        sql.Append(CultureInfo.InvariantCulture, $" FROM {definition.Name} WHERE {whereClause} ORDER BY rowid ASC LIMIT {limit.ToString(CultureInfo.InvariantCulture)} OFFSET {offset.ToString(CultureInfo.InvariantCulture)};\n");

        string output = ExecuteSqlite(sql.ToString());
        long totalRows = 0;
        List<StoredFieldRow> rows = [];
        foreach (string line in TabLines(output))
        {
            string[] cols = line.Split('\t');
            if (cols.Length < 3)
            {
                throw new StoredDataException("Empty row returned from the stored-fields rows query.");
            }

            switch (cols[1])
            {
                case "c":
                    if (cols.Length != 3)
                    {
                        throw new StoredDataException($"Expected 3 columns for the {definition.Name} count, got {cols.Length.ToString(CultureInfo.InvariantCulture)}.");
                    }

                    totalRows = ParseInt64Required(cols[2], $"{definition.Name}.row_count");
                    break;
                case "k":
                    rows.Add(ParseStoredFieldRow(definition, cols[2..]));
                    break;
                default:
                    throw new StoredDataException($"Unexpected row tag '{cols[1]}' for '{definition.Name}'.");
            }
        }

        return new StoredTableRows(definition.Name, columnNames, totalRows, page, limit, offset, rows);
    }

    /// <inheritdoc />
    public StoredSessionProjection? GetSessionProjection(string sessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        IReadOnlyList<StoredSessionProjection> projections = QuerySessionProjections($"sp.session_id = {TextExpr(sessionId)}");
        return projections.Count == 0 ? null : projections[0];
    }

    /// <inheritdoc />
    public IReadOnlyList<StoredSessionProjection> ListSessionProjections(string? harnessId = null)
    {
        if (harnessId is null)
        {
            return QuerySessionProjections(filterPredicate: null);
        }

        return QuerySessionProjections($"EXISTS (SELECT 1 FROM session s WHERE s.session_id = sp.session_id AND s.harness_id = {TextExpr(harnessId)})");
    }

    /// <inheritdoc />
    public SessionHeaderPageResult SessionHeaderPage(string? harnessId, string? search, int page, int limit)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(page, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);

        StringBuilder sql = new(TabReadScriptHeader);
        AppendSessionRootRule(sql, harnessId);
        sql.Append("page_set AS (SELECT sc.session_id, sp.started_at_utc FROM scope sc JOIN session_projection sp ON sp.session_id = sc.session_id WHERE (");
        sql.Append("sc.parent_session_id IS NULL OR sc.parent_session_id = '' OR NOT EXISTS (SELECT 1 FROM scope p WHERE p.session_id = sc.parent_session_id) OR sc.session_id IN ");
        sql.Append(CyclicSessionSet);
        sql.Append(')');
        if (!string.IsNullOrEmpty(search))
        {
            // The search text is bound as one hex literal, so a quote in it is data, and the ESCAPE
            // clause makes a %, _ or backslash in it match itself.
            string pattern = TextExpr("%" + EscapeLikePattern(search) + "%");
            sql.Append(CultureInfo.InvariantCulture, $" AND (sp.label LIKE {pattern} ESCAPE '\\' OR sp.session_id LIKE {pattern} ESCAPE '\\')");
        }

        sql.Append(')');
        return ReadSessionHeaderPage(sql, "DESC", page, limit);
    }

    /// <inheritdoc />
    public SessionHeaderPageResult SessionChildHeaderPage(string parentSessionId, int page, int limit)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(parentSessionId);
        ArgumentOutOfRangeException.ThrowIfLessThan(page, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);

        StringBuilder sql = new(TabReadScriptHeader);
        AppendSessionRootRule(sql, harnessId: null);
        sql.Append("page_set AS (SELECT sp.session_id, sp.started_at_utc FROM session_projection_parent spp JOIN session_projection sp ON sp.session_id = spp.session_id WHERE ");
        AppendSessionAttachPredicate(sql, $"spp.parent_session_id = {TextExpr(parentSessionId)}");
        sql.Append(')');
        return ReadSessionHeaderPage(sql, "ASC", page, limit);
    }

    /// <inheritdoc />
    public IReadOnlyList<SessionChildPreviewEntry> SessionChildPreview(IReadOnlyCollection<string> parentSessionIds, int perParent)
    {
        ArgumentNullException.ThrowIfNull(parentSessionIds);
        ArgumentOutOfRangeException.ThrowIfLessThan(perParent, 1);
        string[] parents = [.. parentSessionIds.Distinct(StringComparer.Ordinal)];
        if (parents.Length == 0)
        {
            return [];
        }

        StringBuilder sql = new(TabReadScriptHeader);
        AppendSessionRootRule(sql, harnessId: null);
        sql.Append("ranked AS (SELECT spp.parent_session_id, sp.session_id, sp.started_at_utc, ");
        sql.Append("row_number() OVER (PARTITION BY spp.parent_session_id ORDER BY sp.started_at_utc ASC, sp.session_id ASC) AS child_rank, ");
        sql.Append("COUNT(*) OVER (PARTITION BY spp.parent_session_id) AS child_total ");
        sql.Append("FROM session_projection_parent spp JOIN session_projection sp ON sp.session_id = spp.session_id WHERE ");
        AppendSessionAttachPredicate(sql, $"spp.parent_session_id IN ({SessionKeyListFilter(parents)})");
        sql.Append(CultureInfo.InvariantCulture, $"), preview AS (SELECT * FROM ranked WHERE child_rank <= {IntSql(perParent)}) ");
        sql.Append("SELECT 'PREVIEW', hex(CAST(r.parent_session_id AS BLOB)), r.child_total, r.child_rank, hex(CAST(r.session_id AS BLOB)) ");
        sql.Append("FROM preview r JOIN session_projection p ON p.session_id = r.parent_session_id ");
        sql.Append("ORDER BY p.started_at_utc DESC, r.parent_session_id ASC, r.child_rank ASC;\n");

        string output = ExecuteSqlite(sql.ToString());
        List<(string ParentSessionId, int TotalChildren, List<string> Children)> previews = [];
        foreach (string line in TabLines(output))
        {
            string[] cols = SplitTabLine(line, 5, "session child preview");
            if (cols[0] != "PREVIEW")
            {
                throw new StoredDataException($"Unexpected session child preview row tag '{cols[0]}'.");
            }

            string parentSessionId = ParseHexRequired(cols[1], "session_projection_parent.parent_session_id");
            int totalChildren = ParseInt32Required(cols[2], "session child preview total");
            int childRank = ParseInt32Required(cols[3], "session child preview rank");
            string childSessionId = ParseHexRequired(cols[4], "session_projection.session_id");
            if (previews.Count == 0 || !string.Equals(previews[^1].ParentSessionId, parentSessionId, StringComparison.Ordinal))
            {
                previews.Add((parentSessionId, totalChildren, []));
            }

            // Rows arrive ranked 1, 2, … within a parent; a rank out of step means the order the
            // preview promises was not the order the store answered.
            if (childRank != previews[^1].Children.Count + 1 || totalChildren != previews[^1].TotalChildren)
            {
                throw new StoredDataException($"Session child preview rows arrived out of rank order for parent '{parentSessionId}'.");
            }

            previews[^1].Children.Add(childSessionId);
        }

        return [.. previews.Select(static preview => new SessionChildPreviewEntry(preview.ParentSessionId, preview.TotalChildren, preview.Children))];
    }

    /// <inheritdoc />
    public IReadOnlyList<StoredSessionHeader> SessionHeadersByKeys(IReadOnlyCollection<string> sessionIds)
    {
        ArgumentNullException.ThrowIfNull(sessionIds);
        string[] keys = [.. sessionIds.Distinct(StringComparer.Ordinal)];
        if (keys.Length == 0)
        {
            return [];
        }

        string sql = TabReadScriptHeader +
                     "SELECT 'HEADER', COUNT(*) OVER (), " + SessionProjectionHeaderColumns + " " +
                     "FROM session_projection sp " + SessionProjectionHeaderJoins + " " +
                     $"WHERE sp.session_id IN ({SessionKeyListFilter(keys)}) ORDER BY sp.started_at_utc DESC, sp.session_id ASC;\n";

        string output = ExecuteSqlite(sql);
        List<StoredSessionHeader> headers = [];
        foreach (string line in TabLines(output))
        {
            headers.Add(ParseSessionHeaderPageRow(line.Split('\t')).Header);
        }

        return headers;
    }

    /// <summary>
    /// Appends the root rule's common table expressions, ending in a comma so the caller adds
    /// <c>page_set</c>, the set its page is cut from.
    /// </summary>
    /// <param name="sql">Script the expressions are appended to.</param>
    /// <param name="harnessId">Optional harness filter over the session rows, which also scopes the rule.</param>
    /// <remarks>
    /// <para>
    /// <c>scope</c> is every projected session in the harness scope, with its parent link.
    /// <c>keep</c> then drops, round by round, every session that no longer has a candidate child:
    /// a session stands at one round only when it is the parent of a session that stood at the round
    /// before. A session on an acyclic chain loses its last standing child within as many rounds as
    /// its longest descendant chain, which cannot exceed the store's projection count, so the round
    /// after that count holds exactly the sessions on a cycle — those whose parent chain loops
    /// back to them.
    /// </para>
    /// <para>
    /// Parent links are followed across the whole scope, not just the page being read, so a cycle
    /// is recognised even when its members fall on different pages.
    /// </para>
    /// </remarks>
    private static void AppendSessionRootRule(StringBuilder sql, string? harnessId)
    {
        sql.Append("WITH RECURSIVE scope(session_id, parent_session_id) AS (SELECT sp.session_id, spp.parent_session_id FROM session_projection sp LEFT JOIN session_projection_parent spp ON spp.session_id = sp.session_id");
        if (harnessId is not null)
        {
            sql.Append(CultureInfo.InvariantCulture, $" WHERE EXISTS (SELECT 1 FROM session s WHERE s.session_id = sp.session_id AND s.harness_id = {TextExpr(harnessId)})");
        }

        sql.Append("), keep(session_id, round) AS (SELECT session_id, 0 FROM scope UNION ALL SELECT spp.parent_session_id, k.round + 1 FROM keep k JOIN session_projection_parent spp ON spp.session_id = k.session_id WHERE spp.parent_session_id IN (SELECT session_id FROM scope) AND k.round < (SELECT COUNT(*) FROM session_projection) + 1), ");
    }

    /// <summary>
    /// Appends the condition under which the session in <c>sp</c> attaches to a parent: the parent
    /// is a projected session in the scope, and the session is not on a cycle, which attaching
    /// would complete.
    /// </summary>
    /// <param name="sql">Script the condition is appended to.</param>
    /// <param name="parentCondition">Predicate naming the parent, over alias <c>spp</c>.</param>
    private static void AppendSessionAttachPredicate(StringBuilder sql, string parentCondition)
    {
        sql.Append('(');
        sql.Append(parentCondition);
        sql.Append(" AND spp.parent_session_id IN (SELECT session_id FROM scope) AND sp.session_id NOT IN ");
        sql.Append(CyclicSessionSet);
        sql.Append(')');
    }

    /// <summary>
    /// Pages a header read whose common table expressions end in <c>page_set</c>. The total rides
    /// on every row the page returns, so one evaluation of the root rule answers a page that has
    /// rows; a page past the end returns none and asks for the count on its own.
    /// </summary>
    /// <param name="cte">The read's common table expressions, ending in <c>page_set</c>.</param>
    /// <param name="order"><c>ASC</c> for oldest start first, <c>DESC</c> for newest start first; session id breaks ties ascending.</param>
    /// <param name="page">One-based page number.</param>
    /// <param name="limit">Rows on a page.</param>
    /// <returns>The page's headers and the size of <c>page_set</c>.</returns>
    private SessionHeaderPageResult ReadSessionHeaderPage(StringBuilder cte, string order, int page, int limit)
    {
        long offset = ((long)page - 1) * limit;
        string sql = cte +
                     "SELECT 'HEADER', (SELECT COUNT(*) FROM page_set), " + SessionProjectionHeaderColumns + " " +
                     "FROM (SELECT session_id FROM page_set ORDER BY started_at_utc " + order + ", session_id ASC" +
                     $" LIMIT {IntSql(limit)} OFFSET {LongSql(offset)}) page " +
                     "JOIN session_projection sp ON sp.session_id = page.session_id " + SessionProjectionHeaderJoins + " " +
                     "ORDER BY sp.started_at_utc " + order + ", sp.session_id ASC;\n";

        string output = ExecuteSqlite(sql);
        List<StoredSessionHeader> headers = [];
        int total = 0;
        foreach (string line in TabLines(output))
        {
            (StoredSessionHeader header, int rowTotal) = ParseSessionHeaderPageRow(line.Split('\t'));
            headers.Add(header);
            total = rowTotal;
        }

        if (headers.Count == 0)
        {
            string[] countLines = [.. TabLines(ExecuteSqlite(cte + "SELECT 'HEADER', COUNT(*) FROM page_set;\n"))];
            if (countLines.Length != 1)
            {
                throw new StoredDataException($"Expected 1 count row for the session header page, got {countLines.Length.ToString(CultureInfo.InvariantCulture)}.");
            }

            total = ParseInt32Required(SplitTabLine(countLines[0], 2, "session header page count")[1], "session header page total");
        }

        return new SessionHeaderPageResult(headers, total);
    }

    /// <summary>
    /// Parses one row of a paged header read: the <c>HEADER</c> tag, the row set's total, then the
    /// fifteen header columns.
    /// </summary>
    /// <param name="cols">Tab-split columns of the row.</param>
    /// <returns>The header and the total.</returns>
    private static (StoredSessionHeader Header, int Total) ParseSessionHeaderPageRow(string[] cols)
    {
        if (cols.Length != 17 || cols[0] != "HEADER")
        {
            throw new StoredDataException($"Expected 17 columns for HEADER row, got {cols.Length.ToString(CultureInfo.InvariantCulture)}.");
        }

        return (ParseSessionHeader(cols, 2), ParseInt32Required(cols[1], "session header page total"));
    }

    /// <summary>
    /// Parses the fifteen projection header columns <see cref="SessionProjectionHeaderColumns"/>
    /// selects, starting at <paramref name="startIndex"/>.
    /// </summary>
    /// <param name="cols">Tab-split columns of the row.</param>
    /// <param name="startIndex">Index of the session id column.</param>
    /// <returns>The projection header.</returns>
    private static StoredSessionHeader ParseSessionHeader(string[] cols, int startIndex) => new(
        ParseHexRequired(cols[startIndex], "session_projection.session_id"),
        ParseHexRequired(cols[startIndex + 1], "session_projection.label"),
        ParseBoolFlag(cols[startIndex + 2], "session_projection.is_subagent"),
        ParseInt32Required(cols[startIndex + 3], "session_projection.span_count"),
        ParseHexRequired(cols[startIndex + 4], "session_projection.started_at_utc"),
        ParseHexRequired(cols[startIndex + 5], "session_projection.ended_at_utc"),
        ParseNullableHex(cols[startIndex + 6], cols[startIndex + 7], "session_projection_parent.parent_session_id"),
        ParseNullableHex(cols[startIndex + 8], cols[startIndex + 9], "session_projection_agent.agent_name"),
        ParseBoolFlag(cols[startIndex + 10], "session_projection_included_in_parent"),
        ParseNullableHex(cols[startIndex + 11], cols[startIndex + 12], "session_context_unmeasurable.reason"),
        ParseNullableHex(cols[startIndex + 13], cols[startIndex + 14], "session_schema_unmeasurable.reason"));

    /// <summary>
    /// Escapes search text for a SQLite <c>LIKE</c> pattern read with <c>ESCAPE '\'</c>, so a
    /// <c>%</c>, <c>_</c> or backslash in the text matches itself.
    /// </summary>
    /// <param name="search">Search text as typed.</param>
    /// <returns>The text with its wildcard and escape characters escaped.</returns>
    private static string EscapeLikePattern(string search) => search
        .Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("%", "\\%", StringComparison.Ordinal)
        .Replace("_", "\\_", StringComparison.Ordinal);

    /// <summary>Builds the <c>IN (…)</c> list that binds <paramref name="keys"/> as hex literals.</summary>
    /// <param name="keys">Text keys to bind; never empty.</param>
    /// <returns>The comma-separated key literals.</returns>
    private static string SessionKeyListFilter(IReadOnlyCollection<string> keys) =>
        string.Join(", ", keys.Select(static key => TextExpr(key)));

    /// <inheritdoc />
    public IReadOnlyList<StoredSessionTurn> GetSessionTurns(string sessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        string sql = TabReadScriptHeader +
                     $"SELECT position, hex(CAST(span_id AS BLOB)) FROM session_turn WHERE session_id = {TextExpr(sessionId)} ORDER BY position ASC;\n";

        string output = ExecuteSqlite(sql);
        List<StoredSessionTurn> results = [];
        foreach (string line in TabLines(output))
        {
            string[] cols = SplitTabLine(line, 2, "session_turn");
            int position = ParseInt32Required(cols[0], "session_turn.position");
            string spanId = ParseHexRequired(cols[1], "session_turn.span_id");
            results.Add(new StoredSessionTurn(sessionId, position, spanId));
        }

        return results;
    }

    /// <inheritdoc />
    public StoredRun? GetRun(string runId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        IReadOnlyList<StoredRun> runs = QueryRuns($"r.run_id = {TextExpr(runId)}");
        return runs.Count == 0 ? null : runs[0];
    }

    /// <inheritdoc />
    public IReadOnlyList<StoredRun> ListRuns(string? harnessId = null) =>
        QueryRuns(harnessId is null ? null : $"r.harness_id = {TextExpr(harnessId)}");

    /// <inheritdoc />
    public StoredExecution? GetExecution(string executionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executionId);
        IReadOnlyList<StoredExecution> executions = QueryExecutions($"e.execution_id = {TextExpr(executionId)}");
        return executions.Count == 0 ? null : executions[0];
    }

    /// <inheritdoc />
    public IReadOnlyList<StoredExecution> ListExecutionsForRun(string runId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        return QueryExecutions($"e.run_id = {TextExpr(runId)}");
    }

    /// <inheritdoc />
    public IReadOnlyList<StoredExecution> ListExecutionsByHarness(string harnessId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(harnessId);
        return QueryExecutions($"e.harness_id = {TextExpr(harnessId)}");
    }

    /// <inheritdoc />
    public StoredHarnessRollup? GetHarnessRollup(string harnessId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(harnessId);
        IReadOnlyList<StoredHarnessRollup> rollups = QueryHarnessRollups($"harness_id = {TextExpr(harnessId)}");
        return rollups.Count == 0 ? null : rollups[0];
    }

    /// <inheritdoc />
    public IReadOnlyList<StoredHarnessRollup> ListHarnessRollups() => QueryHarnessRollups(filterPredicate: null);

    /// <inheritdoc />
    public StoredFinding? GetFinding(string findingId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(findingId);
        IReadOnlyList<StoredFinding> findings = QueryFindings($"f.finding_id = {TextExpr(findingId)}");
        return findings.Count == 0 ? null : findings[0];
    }

    /// <inheritdoc />
    public IReadOnlyList<StoredFinding> ListFindings(string? runId = null, string? sessionId = null)
    {
        List<string> predicates = [];
        if (runId is not null)
        {
            predicates.Add($"fr.run_id = {TextExpr(runId)}");
        }

        if (sessionId is not null)
        {
            predicates.Add($"fs.session_id = {TextExpr(sessionId)}");
        }

        string? filter = predicates.Count > 0 ? string.Join(" AND ", predicates) : null;
        return QueryFindings(filter);
    }

    /// <inheritdoc />
    public StoredPrediction? GetPrediction(string predictionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(predictionId);
        IReadOnlyList<StoredPrediction> predictions = QueryPredictions($"p.prediction_id = {TextExpr(predictionId)}");
        return predictions.Count == 0 ? null : predictions[0];
    }

    /// <inheritdoc />
    public IReadOnlyList<StoredPrediction> ListPredictions(string? runId = null, string? findingId = null)
    {
        List<string> predicates = [];
        if (runId is not null)
        {
            predicates.Add($"p.run_id = {TextExpr(runId)}");
        }

        if (findingId is not null)
        {
            predicates.Add($"p.finding_id = {TextExpr(findingId)}");
        }

        string? filter = predicates.Count > 0 ? string.Join(" AND ", predicates) : null;
        return QueryPredictions(filter);
    }

    /// <inheritdoc />
    public IReadOnlyList<SessionListFigure> SessionListFigures(string? harnessId = null, IModelPricingLookup? pricing = null)
    {
        IReadOnlyList<CollectedSession> sessions = Sessions(harnessId);
        if (sessions.Count == 0)
        {
            return [];
        }

        // One batch each for the sessions, the
        // content-free record scalars, the projections, the fallback turns of sessions without
        // a projection, and the stored ingest problems. The record batch reads no
        // content-bearing table, so a sessions list never materializes conversation text.
        Dictionary<string, SessionListProjectionShape> shapesBySession = new(StringComparer.Ordinal);
        foreach (StoredSessionProjection projection in ListSessionProjections(harnessId))
        {
            shapesBySession.TryAdd(projection.SessionId, new SessionListProjectionShape(projection.RequestSpanIds.Count, projection.Turns));
        }

        return BuildSessionListFigures(sessions, shapesBySession, pricing);
    }

    /// <inheritdoc />
    public IReadOnlyList<SessionListFigure> SessionListFigures(IReadOnlyCollection<string> sessionKeys, IModelPricingLookup? pricing = null)
    {
        ArgumentNullException.ThrowIfNull(sessionKeys);
        string[] keys = [.. sessionKeys.Distinct(StringComparer.Ordinal)];
        if (keys.Length == 0)
        {
            return [];
        }

        // The same batches as the harness-scoped read, each bound to the named keys by its own
        // statement: the session rows, the content-free record scalars, what the figures take
        // from the projections, the fallback turns of sessions without a projection, and the
        // stored ingest problems. No statement lists the store's sessions or projections.
        string filter = SessionKeyListFilter(keys);
        IReadOnlyList<CollectedSession> sessions = QuerySessions($"s.session_id IN ({filter})", $"u.session_id IN ({filter})");
        if (sessions.Count == 0)
        {
            return [];
        }

        return BuildSessionListFigures(sessions, QuerySessionListProjectionShapes(filter), pricing);
    }

    /// <summary>
    /// Builds one sessions-list figure per session from the batched reads both figures passes
    /// share: content-free record scalars, the stored turns a session without a projection falls
    /// back to, and the stored ingest problems.
    /// </summary>
    /// <param name="sessions">Session rows to build figures for, with their workspace and usage.</param>
    /// <param name="shapesBySession">What each projected session's figure takes from its projection, by session id.</param>
    /// <param name="pricing">Optional pricing lookup overriding default bundled rate tables.</param>
    /// <returns>One figure per session, in the order the sessions were read.</returns>
    private IReadOnlyList<SessionListFigure> BuildSessionListFigures(
        IReadOnlyList<CollectedSession> sessions,
        IReadOnlyDictionary<string, SessionListProjectionShape> shapesBySession,
        IModelPricingLookup? pricing)
    {
        List<string> sessionKeys = [.. sessions.Select(static s => s.SessionKey)];
        Dictionary<string, List<CollectedRecord>> recordsBySession = QueryContentFreeRecordsBySession(sessionKeys);
        List<string> missingProjectionKeys = [.. sessionKeys.Where(key => !shapesBySession.ContainsKey(key))];
        Dictionary<string, IReadOnlyList<StoredSessionTurn>> fallbackTurnsBySession = QuerySessionTurnsBySession(missingProjectionKeys);
        Dictionary<string, List<Problem>> problemsBySession = QuerySessionIngestProblemsBySession(sessionKeys);

        List<SessionListFigure> figures = new(sessions.Count);
        foreach (CollectedSession session in sessions)
        {
            IReadOnlyList<CollectedRecord> records = recordsBySession.TryGetValue(session.SessionKey, out List<CollectedRecord>? sessionRecords)
                ? sessionRecords
                : [];
            shapesBySession.TryGetValue(session.SessionKey, out SessionListProjectionShape? shape);
            IReadOnlyList<StoredSessionTurn> projectedTurns = shape?.Turns
                ?? (fallbackTurnsBySession.TryGetValue(session.SessionKey, out IReadOnlyList<StoredSessionTurn>? fallbackTurns) ? fallbackTurns : []);
            IReadOnlyList<Problem> storedProblems = problemsBySession.TryGetValue(session.SessionKey, out List<Problem>? stored) ? stored : [];
            figures.Add(BuildSessionListFigure(session, records, shape?.RequestSpanCount, projectedTurns, storedProblems, pricing));
        }

        return figures;
    }

    /// <summary>
    /// Reads what a session's list figure takes from its projection, for the keyed figures pass:
    /// whether the projection exists, its request span ids, and its turn entries. No context,
    /// schema or timeline row is read.
    /// </summary>
    /// <param name="filter">The <c>IN (…)</c> list binding the session keys.</param>
    /// <returns>Each named session that has a projection, by session id.</returns>
    private Dictionary<string, SessionListProjectionShape> QuerySessionListProjectionShapes(string filter)
    {
        string sql = TabReadScriptHeader +
                     $"SELECT 'PROJ', hex(CAST(sp.session_id AS BLOB)) FROM session_projection sp WHERE sp.session_id IN ({filter});\n" +
                     $"SELECT 'REQ', hex(CAST(session_id AS BLOB)), hex(CAST(request_span_id AS BLOB)) FROM session_request WHERE session_id IN ({filter}) ORDER BY session_id ASC, position ASC;\n" +
                     $"SELECT 'TURN', hex(CAST(session_id AS BLOB)), position, hex(CAST(span_id AS BLOB)) FROM session_turn WHERE session_id IN ({filter}) ORDER BY session_id ASC, position ASC;\n";

        string output = ExecuteSqlite(sql);
        HashSet<string> projectedSessionIds = [];
        Dictionary<string, List<string>> requestsBySession = new(StringComparer.Ordinal);
        Dictionary<string, List<StoredSessionTurn>> turnsBySession = new(StringComparer.Ordinal);
        foreach (string line in TabLines(output))
        {
            string tag = line.Split('\t')[0];
            switch (tag)
            {
                case "PROJ":
                    _ = projectedSessionIds.Add(ParseHexRequired(SplitTabLine(line, 2, "session projection shape")[1], "session_projection.session_id"));
                    break;

                case "REQ":
                    {
                        string[] cols = SplitTabLine(line, 3, "session_request");
                        GetOrCreateList(requestsBySession, ParseHexRequired(cols[1], "session_request.session_id"))
                            .Add(ParseHexRequired(cols[2], "session_request.request_span_id"));
                        break;
                    }

                case "TURN":
                    {
                        string[] cols = SplitTabLine(line, 4, "session_turn");
                        string sessionId = ParseHexRequired(cols[1], "session_turn.session_id");
                        GetOrCreateList(turnsBySession, sessionId)
                            .Add(new StoredSessionTurn(sessionId, ParseInt32Required(cols[2], "session_turn.position"), ParseHexRequired(cols[3], "session_turn.span_id")));
                        break;
                    }

                default:
                    throw new StoredDataException($"Unexpected session projection shape row tag '{tag}'.");
            }
        }

        Dictionary<string, SessionListProjectionShape> shapes = new(projectedSessionIds.Count, StringComparer.Ordinal);
        foreach (string sessionId in projectedSessionIds)
        {
            shapes[sessionId] = new SessionListProjectionShape(
                requestsBySession.TryGetValue(sessionId, out List<string>? requests) ? requests.Count : 0,
                turnsBySession.TryGetValue(sessionId, out List<StoredSessionTurn>? turns) ? turns : []);
        }

        return shapes;
    }

    /// <inheritdoc />
    public SessionSummaryFigure? SessionSummaryFigures(string sessionKey, IModelPricingLookup? pricing = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionKey);

        CollectedSession? session = GetSession(sessionKey);
        if (session is null)
        {
            return null;
        }

        SessionSummaryFigure summary = BuildSessionFigures(
            session,
            SessionRecordsContentFree(sessionKey),
            GetSessionProjection(sessionKey),
            GetSessionTurns(sessionKey),
            QuerySessionIngestProblems(sessionKey),
            pricing).Summary;
        return summary with { SkillInvocations = SkillsInvoked(sessionKey: sessionKey) };
    }

    /// <inheritdoc />
    public IReadOnlyList<SessionReportFigure> SessionReportFigures(IReadOnlyCollection<string> sessionKeys, IModelPricingLookup? pricing = null)
    {
        ArgumentNullException.ThrowIfNull(sessionKeys);
        string[] keys = [.. sessionKeys.Distinct(StringComparer.Ordinal)];
        if (keys.Length == 0)
        {
            return [];
        }

        // One batch each for the named sessions, their content-free record scalars (tool
        // metadata included — the report prices offered and invoked tools), and their stored
        // turns. The report figures are projection-independent, so no projection, problem, or
        // skill read runs, and no statement lists the store's sessions or projections.
        string filter = SessionKeyListFilter(keys);
        IReadOnlyList<CollectedSession> sessions = QuerySessions($"s.session_id IN ({filter})", $"u.session_id IN ({filter})");
        if (sessions.Count == 0)
        {
            return [];
        }

        List<string> pricedKeys = [.. sessions.Select(static s => s.SessionKey)];
        Dictionary<string, List<CollectedRecord>> recordsBySession = QueryContentFreeRecordsBySession(pricedKeys, includeToolMetadata: true);
        Dictionary<string, IReadOnlyList<StoredSessionTurn>> turnsBySession = QuerySessionTurnsBySession(pricedKeys);

        List<SessionReportFigure> figures = new(sessions.Count);
        foreach (CollectedSession session in sessions)
        {
            IReadOnlyList<CollectedRecord> records = recordsBySession.TryGetValue(session.SessionKey, out List<CollectedRecord>? sessionRecords)
                ? sessionRecords
                : [];
            IReadOnlyList<StoredSessionTurn> turns = turnsBySession.TryGetValue(session.SessionKey, out IReadOnlyList<StoredSessionTurn>? sessionTurns)
                ? sessionTurns
                : [];
            figures.Add(BuildSessionReportFigure(session, records, turns, pricing));
        }

        return figures;
    }

    /// <summary>
    /// Projects one session's report figures from the shared summary builder with the projection
    /// left unread: every field the report prices — the token totals, the tool names, and the two
    /// cost summaries — is computed from the content-free records and the stored turns alone, so
    /// the batched read answers exactly what <see cref="SessionSummaryFigures"/> answers for the
    /// session, byte for byte.
    /// </summary>
    private static SessionReportFigure BuildSessionReportFigure(
        CollectedSession session,
        IReadOnlyList<CollectedRecord> records,
        IReadOnlyList<StoredSessionTurn> projectedTurns,
        IModelPricingLookup? pricing)
    {
        SessionSummaryFigure summary = BuildSessionFigures(session, records, null, projectedTurns, [], pricing).Summary;
        return new SessionReportFigure(summary);
    }

    /// <inheritdoc />
    public ReadTimeTurnSummary? TurnFigures(string sessionKey, int turnIndex, IModelPricingLookup? pricing = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionKey);
        if (turnIndex < 0)
        {
            return null;
        }

        CollectedSession? session = GetSession(sessionKey);
        IReadOnlyList<CollectedRecord> records = SessionRecordsContentFree(sessionKey);
        if (session is null && records.Count == 0)
        {
            return null;
        }

        string harnessId = session is not null ? session.HarnessId : records[0].HarnessId;
        IReadOnlyList<ReadTimeTurnSummary> turns =
            BuildContentFreeTurnSummaries(GetSessionTurns(sessionKey), records, harnessId, pricing);
        return turnIndex < turns.Count ? turns[turnIndex] : null;
    }

    /// <inheritdoc />
    public SessionTurnFigureSet? SessionTurnFigures(string sessionKey, IModelPricingLookup? pricing = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionKey);

        CollectedSession? session = GetSession(sessionKey);
        IReadOnlyList<CollectedRecord> records = SessionRecordsContentFree(sessionKey);
        if (session is null && records.Count == 0)
        {
            return null;
        }

        string harnessId = session is not null ? session.HarnessId : records[0].HarnessId;
        IReadOnlyList<StoredSessionTurn> projectedTurns = GetSessionTurns(sessionKey);
        IReadOnlyList<ReadTimeTurnSummary> turns =
            BuildContentFreeTurnSummaries(projectedTurns, records, harnessId, pricing);
        ReadTimeContextBucketSummary? firstContextBucket = null;
        ReadTimeContextBucketSummary? lastContextBucket = null;
        if (GetSessionProjection(sessionKey)?.Context?.Turns is { Count: > 0 } contextTurns)
        {
            List<StoredSessionContextTurn> orderedContext = [.. contextTurns.OrderBy(static t => t.Position)];
            List<(int Position, CollectedRecord Record)> turnEntries =
                ResolveSessionTurnEntriesForReader(projectedTurns, records);
            firstContextBucket = BuildReaderContextBucketSummary(orderedContext[0], turnEntries);
            lastContextBucket = BuildReaderContextBucketSummary(orderedContext[^1], turnEntries);
        }

        return new SessionTurnFigureSet(turns, firstContextBucket, lastContextBucket);
    }

    /// <inheritdoc />
    public SessionRequestFigurePageResult SessionRequestFigures(string sessionKey, int page, int limit)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionKey);
        ArgumentOutOfRangeException.ThrowIfLessThan(page, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);

        StringBuilder ctes = new("WITH RECURSIVE ");
        AppendSessionSectionCtes(ctes, TextExpr(sessionKey));
        ctes.Append(CultureInfo.InvariantCulture, $"named_requests(position, request_span_id) AS (SELECT position, request_span_id FROM session_request WHERE session_id = {TextExpr(sessionKey)}), ");
        ctes.Append("fallback_requests(position, request_span_id) AS (SELECT ROW_NUMBER() OVER (ORDER BY rr.started_at_utc ASC, rr.span_id ASC), rr.span_id FROM roots ro JOIN record rr ON rr.span_id = ro.span_id WHERE NOT EXISTS (SELECT 1 FROM session_request WHERE session_id = ");
        ctes.Append(TextExpr(sessionKey));
        ctes.Append(")), ");
        ctes.Append("all_requests(position, request_span_id) AS (SELECT position, request_span_id FROM named_requests WHERE EXISTS (SELECT 1 FROM session_request WHERE session_id = ");
        ctes.Append(TextExpr(sessionKey));
        ctes.Append(") UNION ALL SELECT position, request_span_id FROM fallback_requests) ");

        StringBuilder sql = new(TabReadScriptHeader);
        sql.Append(ctes);
        sql.Append(CultureInfo.InvariantCulture, $", page(position, request_span_id) AS (SELECT position, request_span_id FROM all_requests ORDER BY position ASC LIMIT {IntSql(limit)} OFFSET {LongSql(((long)page - 1) * limit)}), ");
        sql.Append("subtree(root, span) AS (SELECT request_span_id, request_span_id FROM page UNION SELECT s.root, ch.span_id FROM subtree s JOIN record_parent ch ON ch.parent_span_id = s.span AND ch.span_id IN (SELECT span_id FROM spans)), ");
        sql.Append("owned(root, turn_position, turn_span) AS (SELECT s.root, t.position, t.span_id FROM subtree s JOIN turn_entries t ON t.span_id = s.span), ");
        sql.Append("ranked(root, turn_position, turn_span, turn_rank, turn_total) AS (SELECT root, turn_position, turn_span, ROW_NUMBER() OVER (PARTITION BY root ORDER BY turn_position ASC), COUNT(*) OVER (PARTITION BY root) FROM owned), ");
        sql.Append("first_model(root, model_id) AS (SELECT root, model_id FROM (SELECT o.root, rm.model_id, ROW_NUMBER() OVER (PARTITION BY o.root ORDER BY o.turn_position ASC) AS model_rank FROM owned o JOIN record_model rm ON rm.span_id = o.turn_span) WHERE model_rank = 1) ");

        sql.Append("SELECT 'REQROW', (SELECT COUNT(*) FROM all_requests), hex(CAST(p.request_span_id AS BLOB)), ");
        sql.Append("typeof(rr.started_at_utc), COALESCE(hex(CAST(rr.started_at_utc AS BLOB)), ''), ");
        sql.Append("typeof(rm.model_id), COALESCE(hex(CAST(rm.model_id AS BLOB)), ''), ");
        sql.Append("COALESCE(rk.turn_total, 0), ");
        sql.Append("typeof(fts.started_at_utc), COALESCE(hex(CAST(fts.started_at_utc AS BLOB)), ''), ");
        sql.Append("typeof(fmm.model_id), COALESCE(hex(CAST(fmm.model_id AS BLOB)), '') ");
        sql.Append("FROM page p ");
        sql.Append("LEFT JOIN record rr ON rr.span_id = p.request_span_id ");
        sql.Append("LEFT JOIN record_model rm ON rm.span_id = p.request_span_id ");
        sql.Append("LEFT JOIN (SELECT root, MAX(turn_total) AS turn_total FROM ranked GROUP BY root) rk ON rk.root = p.request_span_id ");
        sql.Append("LEFT JOIN (SELECT root, turn_span FROM ranked WHERE turn_rank = 1) ft ON ft.root = p.request_span_id ");
        sql.Append("LEFT JOIN record fts ON fts.span_id = ft.turn_span ");
        sql.Append("LEFT JOIN first_model fmm ON fmm.root = p.request_span_id ");
        sql.Append("ORDER BY p.position ASC;\n");

        string output = ExecuteSqlite(sql.ToString());
        List<ReadTimeSessionRequestSummary> rows = [];
        int total = 0;
        foreach (string line in TabLines(output))
        {
            string[] cols = SplitTabLine(line, 12, "session request figures");
            total = ParseInt32Required(cols[1], "session request page total");
            string requestSpanId = ParseHexRequired(cols[2], "session_request.request_span_id");
            string? requestStartedRaw = ParseNullableHex(cols[3], cols[4], "record.started_at_utc");
            DateTimeOffset? requestStarted = requestStartedRaw is null ? null : ParseTimestampRequired(requestStartedRaw, "record.started_at_utc");
            string? requestModel = ParseNullableHex(cols[5], cols[6], "record_model.model_id");
            string? firstTurnStartedRaw = ParseNullableHex(cols[8], cols[9], "owned turn started_at_utc");
            DateTimeOffset? firstTurnStarted = firstTurnStartedRaw is null ? null : ParseTimestampRequired(firstTurnStartedRaw, "owned turn started_at_utc");
            string? firstTurnModel = ParseNullableHex(cols[10], cols[11], "owned turn model_id");
            string? model = string.IsNullOrEmpty(requestModel) ? firstTurnModel : requestModel;
            string timestampUtc = requestStarted.HasValue
                ? FormatTimestamp(requestStarted.Value)
                : (firstTurnStarted.HasValue ? FormatTimestamp(firstTurnStarted.Value) : string.Empty);
            rows.Add(new ReadTimeSessionRequestSummary(
                RequestSpanId: requestSpanId,
                TimestampUtc: timestampUtc,
                Turns: ParseInt32Required(cols[7], "owned turn count"),
                Model: model,
                PromptPreview: null));
        }

        if (rows.Count == 0)
        {
            total = ReadSessionSectionTotal(ctes, "all_requests", "session request page");
        }

        return new SessionRequestFigurePageResult(rows, total);
    }

    /// <inheritdoc />
    public SessionReconciliationFigurePageResult SessionReconciliationFigures(string sessionKey, int page, int limit)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionKey);
        ArgumentOutOfRangeException.ThrowIfLessThan(page, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);

        StringBuilder ctes = new("WITH RECURSIVE ");
        AppendSessionSectionCtes(ctes, TextExpr(sessionKey));
        ctes.Append("turn_tokens(position, span_id, fresh_input, cache_read, cache_write, output) AS (SELECT t.position, t.span_id, ");
        ctes.Append("MAX(CASE WHEN tc.token_class = 'input' THEN tc.token_count END), ");
        ctes.Append("MAX(CASE WHEN tc.token_class = 'cache_read' THEN tc.token_count END), ");
        ctes.Append("MAX(CASE WHEN tc.token_class = 'cache_write' THEN tc.token_count END), ");
        ctes.Append("MAX(CASE WHEN tc.token_class = 'output' THEN tc.token_count END) ");
        ctes.Append("FROM turn_entries t LEFT JOIN record_token_count tc ON tc.span_id = t.span_id GROUP BY t.position, t.span_id), ");
        ctes.Append("turn_reported(position, span_id, reported_input, reported_output) AS (SELECT t.position, t.span_id, ");
        ctes.Append("MAX(CASE WHEN rt.direction = 'input' THEN rt.token_count END), ");
        ctes.Append("MAX(CASE WHEN rt.direction = 'output' THEN rt.token_count END) ");
        ctes.Append("FROM turn_entries t LEFT JOIN record_reported_total rt ON rt.span_id = t.span_id GROUP BY t.position, t.span_id), ");
        ctes.Append("reconciled(position, span_id, reported_input, sum_chat_input, input_match, reported_output, sum_chat_output, output_match) AS (SELECT s.position, s.span_id, r.reported_input, ");
        ctes.Append("CASE WHEN s.fresh_input IS NULL AND s.cache_read IS NULL AND s.cache_write IS NULL THEN NULL ");
        ctes.Append("ELSE COALESCE(s.fresh_input, 0) + COALESCE(s.cache_read, 0) + COALESCE(s.cache_write, 0) END, ");
        ctes.Append("CASE WHEN r.reported_input IS NULL OR (s.fresh_input IS NULL AND s.cache_read IS NULL AND s.cache_write IS NULL) THEN NULL ");
        ctes.Append("ELSE r.reported_input = COALESCE(s.fresh_input, 0) + COALESCE(s.cache_read, 0) + COALESCE(s.cache_write, 0) END, ");
        ctes.Append("r.reported_output, s.output, ");
        ctes.Append("CASE WHEN r.reported_output IS NULL OR s.output IS NULL THEN NULL ELSE r.reported_output = s.output END ");
        ctes.Append("FROM turn_tokens s JOIN turn_reported r ON r.position = s.position AND r.span_id = s.span_id), ");
        ctes.Append("measurable AS (SELECT * FROM reconciled WHERE input_match IS NOT NULL AND output_match IS NOT NULL) ");

        StringBuilder sql = new(TabReadScriptHeader);
        sql.Append(ctes);
        sql.Append("SELECT 'RECON', (SELECT COUNT(*) FROM measurable), position, hex(CAST(span_id AS BLOB)), ");
        sql.Append("typeof(reported_input), COALESCE(CAST(reported_input AS TEXT), ''), ");
        sql.Append("sum_chat_input, input_match, ");
        sql.Append("typeof(reported_output), COALESCE(CAST(reported_output AS TEXT), ''), ");
        sql.Append("sum_chat_output, output_match ");
        sql.Append(CultureInfo.InvariantCulture, $"FROM measurable ORDER BY position ASC LIMIT {IntSql(limit)} OFFSET {LongSql(((long)page - 1) * limit)};\n");

        string output = ExecuteSqlite(sql.ToString());
        List<ReadTimeTurnReconciliation> rows = [];
        int total = 0;
        foreach (string line in TabLines(output))
        {
            string[] cols = SplitTabLine(line, 12, "session reconciliation figures");
            total = ParseInt32Required(cols[1], "session reconciliation page total");
            rows.Add(new ReadTimeTurnReconciliation(
                RequestSpanId: ParseHexRequired(cols[3], "reconciled turn span_id"),
                RootInput: ParseNullableInt64(cols[4], cols[5], "record_reported_total.input"),
                SumChatInput: ParseInt64Required(cols[6], "tokenized input"),
                InputMatch: ParseBoolFlag(cols[7], "reconciled input_match"),
                RootOutput: ParseNullableInt64(cols[8], cols[9], "record_reported_total.output"),
                SumChatOutput: ParseInt64Required(cols[10], "tokenized output"),
                OutputMatch: ParseBoolFlag(cols[11], "reconciled output_match")));
        }

        if (rows.Count == 0)
        {
            total = ReadSessionSectionTotal(ctes, "measurable", "session reconciliation page");
        }

        return new SessionReconciliationFigurePageResult(rows, total);
    }

    /// <inheritdoc />
    public SessionProblemPageResult SessionProblems(string sessionKey, int page, int limit)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionKey);
        ArgumentOutOfRangeException.ThrowIfLessThan(page, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);

        StringBuilder sql = new(TabReadScriptHeader);
        sql.Append("SELECT 'PROBLEM', (SELECT COUNT(*) FROM (SELECT DISTINCT problem_id FROM (");
        AppendSessionProblemAttachment(sql, sessionKey);
        sql.Append(")) att), ");
        sql.Append("hex(CAST(ip.severity AS BLOB)), hex(CAST(ip.code AS BLOB)), hex(CAST(ip.message AS BLOB)), ");
        sql.Append("typeof(ipl.location), COALESCE(hex(CAST(ipl.location AS BLOB)), ''), ");
        sql.Append("typeof(ips.span_id), COALESCE(hex(CAST(ips.span_id AS BLOB)), ''), ");
        sql.Append("typeof(ipst.stack), COALESCE(hex(CAST(ipst.stack AS BLOB)), '') ");
        sql.Append("FROM ingest_problem ip ");
        sql.Append("LEFT JOIN ingest_problem_location ipl ON ipl.problem_id = ip.problem_id ");
        sql.Append("LEFT JOIN ingest_problem_span ips ON ips.problem_id = ip.problem_id ");
        sql.Append("LEFT JOIN ingest_problem_stack ipst ON ipst.problem_id = ip.problem_id ");
        sql.Append("WHERE ip.problem_id IN (SELECT DISTINCT problem_id FROM (");
        AppendSessionProblemAttachment(sql, sessionKey);
        sql.Append(")) ");
        sql.Append(CultureInfo.InvariantCulture, $"ORDER BY ip.problem_id ASC LIMIT {IntSql(limit)} OFFSET {LongSql(((long)page - 1) * limit)};\n");

        string output = ExecuteSqlite(sql.ToString());
        List<Problem> rows = [];
        int total = 0;
        foreach (string line in TabLines(output))
        {
            string[] cols = SplitTabLine(line, 11, "session ingest_problem page");
            total = ParseInt32Required(cols[1], "session problem page total");
            ProblemSeverity severity = ParseProblemSeverityValue(ParseHexRequired(cols[2], "ingest_problem.severity"));
            string code = ParseHexRequired(cols[3], "ingest_problem.code");
            string message = ParseHexRequired(cols[4], "ingest_problem.message");
            string? location = ParseNullableHex(cols[5], cols[6], "ingest_problem_location.location")
                ?? ParseNullableHex(cols[7], cols[8], "ingest_problem_span.span_id");
            string? stack = ParseNullableHex(cols[9], cols[10], "ingest_problem_stack.stack");
            rows.Add(new Problem(severity, code, message, location, stack));
        }

        if (rows.Count == 0)
        {
            StringBuilder countSql = new(TabReadScriptHeader);
            countSql.Append("SELECT 'PROBLEM', COUNT(*) FROM (SELECT DISTINCT problem_id FROM (");
            AppendSessionProblemAttachment(countSql, sessionKey);
            countSql.Append("));\n");
            string[] countLines = [.. TabLines(ExecuteSqlite(countSql.ToString()))];
            if (countLines.Length != 1)
            {
                throw new StoredDataException($"Expected 1 count row for the session problem page, got {countLines.Length.ToString(CultureInfo.InvariantCulture)}.");
            }

            total = ParseInt32Required(SplitTabLine(countLines[0], 2, "session problem page count")[1], "session problem page total");
        }

        return new SessionProblemPageResult(rows, total);
    }

    /// <inheritdoc />
    public SessionToolFigureSet SessionToolFigures(string sessionKey, IModelPricingLookup? pricing = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionKey);
        string key = TextExpr(sessionKey);

        // One script, five tagged selects: the two identity columns the unused-range pricing
        // resolves (the session's harness and its first turn model), the tool-call metadata, the
        // tool-definition metadata, and the stored schema projection. No content column, no
        // content-text table, no timeline, and no context row is touched.
        StringBuilder sql = new(TabReadScriptHeader);
        sql.Append(CultureInfo.InvariantCulture, $"SELECT 'HARNESS', typeof(s.harness_id), COALESCE(hex(CAST(s.harness_id AS BLOB)), '') FROM session s WHERE s.session_id = {key};\n");
        sql.Append(CultureInfo.InvariantCulture, $"SELECT 'MODEL', typeof(rm.model_id), COALESCE(hex(CAST(rm.model_id AS BLOB)), '') FROM record_session_link rsl JOIN record r ON r.span_id = rsl.span_id JOIN record_model rm ON rm.span_id = r.span_id WHERE rsl.session_key = {key} AND r.op = 'llm.invoke' ORDER BY r.started_at_utc ASC, r.span_id ASC LIMIT 1;\n");
        sql.Append(CultureInfo.InvariantCulture, $"SELECT 'CALL', hex(CAST(tc.span_id AS BLOB)), hex(CAST(tc.tool_name AS BLOB)), typeof(oco.succeeded), COALESCE(CAST(oco.succeeded AS TEXT), ''), typeof(terr.message), COALESCE(hex(CAST(terr.message AS BLOB)), '') FROM tool_call tc JOIN record_session_link rsl ON rsl.span_id = tc.span_id AND rsl.session_key = {key} LEFT JOIN tool_call_outcome oco ON oco.span_id = tc.span_id LEFT JOIN tool_call_error terr ON terr.span_id = tc.span_id ORDER BY tc.rowid ASC;\n");
        sql.Append(CultureInfo.InvariantCulture, $"SELECT 'DEF', hex(CAST(td.span_id AS BLOB)), td.position, hex(CAST(td.tool_name AS BLOB)), td.schema_tokens, typeof(tds.server_name), COALESCE(hex(CAST(tds.server_name AS BLOB)), '') FROM tool_definition td JOIN record_session_link rsl ON rsl.span_id = td.span_id AND rsl.session_key = {key} JOIN record r ON r.span_id = td.span_id LEFT JOIN tool_definition_server tds ON tds.span_id = td.span_id AND tds.position = td.position ORDER BY r.rowid ASC, td.position ASC;\n");
        sql.Append(CultureInfo.InvariantCulture, $"SELECT 'SCH', t.invocation_count, t.turn_count, typeof(u.token_residencies), COALESCE(CAST(u.token_residencies AS TEXT), '') FROM session_schema t LEFT JOIN session_schema_unused u ON u.session_id = t.session_id WHERE t.session_id = {key};\n");
        sql.Append(CultureInfo.InvariantCulture, $"SELECT 'SCH_REASON', typeof(r.reason), COALESCE(hex(CAST(r.reason AS BLOB)), '') FROM session_schema_unmeasurable r WHERE r.session_id = {key};\n");
        sql.Append(CultureInfo.InvariantCulture, $"SELECT 'SCH_TOOL', t.position, hex(CAST(t.tool_name AS BLOB)), t.cost_tokens, t.invoked, typeof(s.server_name), COALESCE(hex(CAST(s.server_name AS BLOB)), '') FROM session_schema_ranked_tool t LEFT JOIN session_schema_ranked_tool_server s ON s.session_id = t.session_id AND s.position = t.position WHERE t.session_id = {key} ORDER BY t.position ASC;\n");
        sql.Append(CultureInfo.InvariantCulture, $"SELECT 'SCH_SRV', hex(CAST(t.server_name AS BLOB)), t.tokens FROM session_schema_server t WHERE t.session_id = {key} ORDER BY t.rowid ASC;\n");

        string output = ExecuteSqlite(sql.ToString());
        string? harnessId = null;
        string? primaryModel = null;
        int toolCallTotal = 0;
        List<string> toolsInvoked = [];
        HashSet<string> seenInvokedTools = new(StringComparer.Ordinal);
        Dictionary<string, int> invCounts = new(StringComparer.Ordinal);
        Dictionary<string, int> errCounts = new(StringComparer.Ordinal);
        Dictionary<string, (long SchemaTokens, string? Server, HashSet<string> SpanIds)> defStats = new(StringComparer.Ordinal);
        List<string> toolsOffered = [];
        HashSet<string> seenOfferedTools = new(StringComparer.Ordinal);
        StoredSessionSchema? schema = null;
        List<StoredSchemaRankedTool> rankedTools = [];
        List<StoredSchemaServerTokens> schemaServers = [];
        string? definitionsSpanId = null;
        string? schemaUnmeasurableReason = null;

        foreach (string line in TabLines(output))
        {
            string[] cols = line.Split('\t');
            if (cols.Length < 2)
            {
                throw new StoredDataException("Unexpected short row in the session tool figures read.");
            }

            switch (cols[0])
            {
                case "HARNESS":
                    if (cols.Length != 3)
                    {
                        throw new StoredDataException($"Expected 3 columns for HARNESS row, got {cols.Length.ToString(CultureInfo.InvariantCulture)}.");
                    }

                    harnessId = ParseNullableHex(cols[1], cols[2], "session.harness_id");
                    break;

                case "MODEL":
                    if (cols.Length != 3)
                    {
                        throw new StoredDataException($"Expected 3 columns for MODEL row, got {cols.Length.ToString(CultureInfo.InvariantCulture)}.");
                    }

                    primaryModel = ParseNullableHex(cols[1], cols[2], "record_model.model_id");
                    break;

                case "CALL":
                    if (cols.Length != 7)
                    {
                        throw new StoredDataException($"Expected 7 columns for CALL row, got {cols.Length.ToString(CultureInfo.InvariantCulture)}.");
                    }

                    toolCallTotal++;
                    string callToolName = ParseHexRequired(cols[2], "tool_call.tool_name");
                    if (seenInvokedTools.Add(callToolName))
                    {
                        toolsInvoked.Add(callToolName);
                    }

                    invCounts[callToolName] = invCounts.GetValueOrDefault(callToolName) + 1;
                    bool? succeeded = ParseOptionalBool(cols[3], cols[4], "tool_call_outcome.succeeded");
                    string? callError = ParseNullableHex(cols[5], cols[6], "tool_call_error.message");
                    if (succeeded == false || callError is not null)
                    {
                        errCounts[callToolName] = errCounts.GetValueOrDefault(callToolName) + 1;
                    }

                    break;

                case "DEF":
                    if (cols.Length != 7)
                    {
                        throw new StoredDataException($"Expected 7 columns for DEF row, got {cols.Length.ToString(CultureInfo.InvariantCulture)}.");
                    }

                    string defSpan = ParseHexRequired(cols[1], "tool_definition.span_id");
                    definitionsSpanId ??= defSpan;
                    string defToolName = ParseHexRequired(cols[3], "tool_definition.tool_name");
                    long schemaTokens = ParseInt64Required(cols[4], "tool_definition.schema_tokens");
                    string? defServer = ParseNullableHex(cols[5], cols[6], "tool_definition_server.server_name");
                    if (seenOfferedTools.Add(defToolName))
                    {
                        toolsOffered.Add(defToolName);
                    }

                    if (!defStats.TryGetValue(defToolName, out (long SchemaTokens, string? Server, HashSet<string> SpanIds) existing))
                    {
                        defStats[defToolName] = (schemaTokens, defServer, new HashSet<string>(StringComparer.Ordinal) { defSpan });
                    }
                    else
                    {
                        _ = existing.SpanIds.Add(defSpan);
                        defStats[defToolName] = (existing.SchemaTokens, existing.Server ?? defServer, existing.SpanIds);
                    }

                    break;

                case "SCH":
                    if (cols.Length != 5)
                    {
                        throw new StoredDataException($"Expected 5 columns for SCH row, got {cols.Length.ToString(CultureInfo.InvariantCulture)}.");
                    }

                    schema = new StoredSessionSchema(
                        InvocationCount: ParseInt32Required(cols[1], "session_schema.invocation_count"),
                        TurnCount: ParseInt32Required(cols[2], "session_schema.turn_count"),
                        RankedTools: rankedTools,
                        Servers: schemaServers,
                        UnusedTokenResidencies: ParseNullableInt64(cols[3], cols[4], "session_schema_unused.token_residencies"));
                    break;

                case "SCH_REASON":
                    if (cols.Length != 3)
                    {
                        throw new StoredDataException($"Expected 3 columns for SCH_REASON row, got {cols.Length.ToString(CultureInfo.InvariantCulture)}.");
                    }

                    schemaUnmeasurableReason = ParseNullableHex(cols[1], cols[2], "session_schema_unmeasurable.reason");
                    break;

                case "SCH_TOOL":
                    if (cols.Length != 7)
                    {
                        throw new StoredDataException($"Expected 7 columns for SCH_TOOL row, got {cols.Length.ToString(CultureInfo.InvariantCulture)}.");
                    }

                    rankedTools.Add(new StoredSchemaRankedTool(
                        Position: ParseInt32Required(cols[1], "session_schema_ranked_tool.position"),
                        ToolName: ParseHexRequired(cols[2], "session_schema_ranked_tool.tool_name"),
                        CostTokens: ParseInt64Required(cols[3], "session_schema_ranked_tool.cost_tokens"),
                        Invoked: ParseBoolFlag(cols[4], "session_schema_ranked_tool.invoked"),
                        ServerName: ParseNullableHex(cols[5], cols[6], "session_schema_ranked_tool_server.server_name")));
                    break;

                case "SCH_SRV":
                    if (cols.Length != 3)
                    {
                        throw new StoredDataException($"Expected 3 columns for SCH_SRV row, got {cols.Length.ToString(CultureInfo.InvariantCulture)}.");
                    }

                    schemaServers.Add(new StoredSchemaServerTokens(
                        ServerName: ParseHexRequired(cols[1], "session_schema_server.server_name"),
                        Tokens: ParseInt64Required(cols[2], "session_schema_server.tokens")));
                    break;

                default:
                    throw new StoredDataException($"Unexpected row tag in the session tool figures read: '{cols[0]}'.");
            }
        }

        // The residency helpers price the stored schema only, so the read carries the schema in a
        // minimal projection shape instead of reading the session's whole projection.
        StoredSessionProjection? projection = schema is null
            ? null
            : new StoredSessionProjection(
                SessionId: sessionKey,
                Label: string.Empty,
                IsSubagent: false,
                SpanCount: 0,
                StartedAtUtc: string.Empty,
                EndedAtUtc: string.Empty,
                ParentSessionId: null,
                AgentName: null,
                IncludedInParent: false,
                RequestSpanIds: [],
                Turns: [],
                Schema: schema);

        List<ReadTimeSessionToolSummary> tools = BuildReaderToolSummaries(
            projection,
            schema?.TurnCount ?? 0,
            defStats,
            toolsInvoked,
            invCounts,
            errCounts);
        List<ReadTimeSessionServerSummary> servers = BuildReaderServerSummaries(projection, tools);
        UnusedSchemaRange? unusedRange = ResolveReaderUnusedRange(
            projection,
            harnessId ?? string.Empty,
            primaryModel is null ? [] : [primaryModel],
            pricing);

        List<StoredSchemaServerTokens> byServer = [];
        if (schema is not null)
        {
            foreach (StoredSchemaServerTokens server in schema.Servers)
            {
                byServer.Add(new StoredSchemaServerTokens(server.ServerName, server.Tokens));
            }
        }
        else
        {
            // The unmeasured ranking's rollup, priced from the tool rows themselves the way the
            // display layer derives it today: rows carrying a server and a measured cost, largest
            // cost first.
            Dictionary<string, long> totals = new(StringComparer.Ordinal);
            foreach (ReadTimeSessionToolSummary tool in tools)
            {
                if (tool.Server is null || tool.TotalSchemaCost <= 0)
                {
                    continue;
                }

                totals[tool.Server] = totals.GetValueOrDefault(tool.Server) + tool.TotalSchemaCost;
            }

            byServer.AddRange(totals
                .OrderByDescending(static kv => kv.Value)
                .ThenBy(static kv => kv.Key, StringComparer.Ordinal)
                .Select(static kv => new StoredSchemaServerTokens(kv.Key, kv.Value)));
        }

        long? maxCost = null;
        long residentCostTotal = 0;
        int unusedCount = 0;
        foreach (ReadTimeSessionToolSummary tool in tools)
        {
            if (!maxCost.HasValue || tool.TotalSchemaCost > maxCost.Value)
            {
                maxCost = tool.TotalSchemaCost;
            }

            residentCostTotal += tool.TotalSchemaCost;
            if (!tool.Invoked)
            {
                unusedCount++;
            }
        }

        return new SessionToolFigureSet(
            Tools: tools,
            Servers: servers,
            ToolsOffered: toolsOffered,
            UnusedRange: unusedRange,
            ByServer: byServer,
            MaxCost: maxCost,
            ResidentCostTotal: residentCostTotal,
            InvocationCount: toolCallTotal,
            UnusedCount: unusedCount,
            DefinitionsSpanId: definitionsSpanId,
            SchemaMeasurable: schema is not null,
            SchemaUnmeasurableReason: schemaUnmeasurableReason);
    }

    /// <inheritdoc />
    public IReadOnlyList<StoredSkillInvocation> SessionSkillFigures(string sessionKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionKey);
        string sql = TabReadScriptHeader +
                     "SELECT hex(CAST(rsi.span_id AS BLOB)), hex(CAST(rsi.skill_name AS BLOB)) " +
                     "FROM record_skill_invocation rsi " +
                     "JOIN record_session_link rsl ON rsl.span_id = rsi.span_id " +
                     $"WHERE rsl.session_key = {TextExpr(sessionKey)} ORDER BY rsi.rowid ASC;\n";

        string output = ExecuteSqlite(sql);
        List<StoredSkillInvocation> results = [];
        foreach (string line in TabLines(output))
        {
            string[] cols = SplitTabLine(line, 2, "session record_skill_invocation page");
            results.Add(new StoredSkillInvocation(
                SpanId: ParseHexRequired(cols[0], "record_skill_invocation.span_id"),
                SkillName: ParseHexRequired(cols[1], "record_skill_invocation.skill_name")));
        }

        return results;
    }

    /// <summary>
    /// The unsplit tool names a KW skill load is recorded under (D17). Together with the MCP
    /// split rule in <see cref="AppendSessionKwLoadPredicate"/>, this list is the statement's
    /// whole load filter, so the page and the total answer from SQL.
    /// </summary>
    private static readonly string[] KwSkillLoadToolNames =
    [
        "mcp__kw-docs__load_skill",
        "mcp__kw_docs__load_skill",
        "kw-docs_load_skill",
        "kw-docs__load_skill",
        "kw-docs/load_skill",
    ];

    /// <inheritdoc />
    public SessionKwSkillLoadPageResult SessionKwSkillLoads(string sessionKey, int page, int limit)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionKey);
        ArgumentOutOfRangeException.ThrowIfLessThan(page, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        // The statement reads the tool-call header, its MCP split, the first 1024 characters of
        // its reply and its record's start time for the session's load rows on the page only: the
        // D17 load rule is the statement's filter and the page answers from LIMIT/OFFSET, so a
        // page never reads the reply head of a load outside it. The call's input is never joined:
        // the skill is read from the reply header (D17).
        StringBuilder sql = new(TabReadScriptHeader);
        sql.Append("SELECT 'KWLOAD', (SELECT COUNT(*) FROM (SELECT tc.span_id FROM tool_call tc ");
        sql.Append("JOIN record r ON r.span_id = tc.span_id ");
        sql.Append("LEFT JOIN tool_call_mcp tcm ON tcm.span_id = tc.span_id ");
        AppendSessionKwLoadPredicate(sql, sessionKey);
        sql.Append(")), ");
        sql.Append("hex(CAST(tc.span_id AS BLOB)), hex(CAST(tc.tool_name AS BLOB)), ");
        sql.Append("typeof(tcm.server_name), COALESCE(hex(CAST(tcm.server_name AS BLOB)), ''), ");
        sql.Append("typeof(tcm.server_tool_name), COALESCE(hex(CAST(tcm.server_tool_name AS BLOB)), ''), ");
        sql.Append("typeof(tcrt.text), COALESCE(hex(CAST(substr(tcrt.text, 1, 1024) AS BLOB)), ''), ");
        sql.Append("hex(CAST(r.started_at_utc AS BLOB)) ");
        sql.Append("FROM tool_call tc ");
        sql.Append("JOIN record r ON r.span_id = tc.span_id ");
        sql.Append("LEFT JOIN tool_call_mcp tcm ON tcm.span_id = tc.span_id ");
        sql.Append("LEFT JOIN tool_call_result_text tcrt ON tcrt.span_id = tc.span_id ");
        AppendSessionKwLoadPredicate(sql, sessionKey);
        sql.Append(CultureInfo.InvariantCulture, $" ORDER BY tc.rowid ASC LIMIT {IntSql(limit)} OFFSET {LongSql(((long)page - 1) * limit)};\n");

        string output = ExecuteSqlite(sql.ToString());
        List<StoredKwSkillLoad> results = [];
        int total = 0;
        foreach (string line in TabLines(output))
        {
            string[] cols = SplitTabLine(line, 11, "session KW skill load page");
            total = ParseInt32Required(cols[1], "session KW skill load page total");
            results.Add(new StoredKwSkillLoad(
                SpanId: ParseHexRequired(cols[2], "tool_call.span_id"),
                ToolName: ParseHexRequired(cols[3], "tool_call.tool_name"),
                McpServerName: ParseNullableHex(cols[4], cols[5], "tool_call_mcp.server_name"),
                McpToolName: ParseNullableHex(cols[6], cols[7], "tool_call_mcp.server_tool_name"),
                ReplyHead: ParseNullableHex(cols[8], cols[9], "tool_call_result_text.text"),
                StartedAtUtc: ParseTimestampRequired(ParseHexRequired(cols[10], "record.started_at_utc"), "record.started_at_utc")));
        }

        if (results.Count == 0)
        {
            StringBuilder countSql = new(TabReadScriptHeader);
            countSql.Append("SELECT 'KWLOAD', COUNT(*) FROM (SELECT tc.span_id FROM tool_call tc ");
            countSql.Append("JOIN record r ON r.span_id = tc.span_id ");
            countSql.Append("LEFT JOIN tool_call_mcp tcm ON tcm.span_id = tc.span_id ");
            AppendSessionKwLoadPredicate(countSql, sessionKey);
            countSql.Append(");\n");
            string[] countLines = [.. TabLines(ExecuteSqlite(countSql.ToString()))];
            if (countLines.Length != 1)
            {
                throw new StoredDataException($"Expected 1 count row for the session KW skill load page, got {countLines.Length.ToString(CultureInfo.InvariantCulture)}.");
            }

            total = ParseInt32Required(SplitTabLine(countLines[0], 2, "session KW skill load count")[1], "session KW skill load page total");
        }

        return new SessionKwSkillLoadPageResult(results, total);
    }

    /// <summary>Appends the D17 load predicate selecting a session's KW skill load rows.</summary>
    /// <remarks>
    /// A load is a kw-docs <c>load_skill</c>: with an MCP split, the server name normalized (lower
    /// case, <c>_</c> to <c>-</c>) names kw-docs and the split tool is <c>load_skill</c>; without
    /// one, the tool name is one of the unsplit D17 spellings. Another server's <c>load_skill</c>
    /// is not a load (D17), so the page and the total count loads only. The application
    /// recognizer keeps the final word on each row the page returns.
    /// </remarks>
    private static void AppendSessionKwLoadPredicate(StringBuilder sql, string sessionKey)
    {
        string unsplitNames = string.Join(", ", KwSkillLoadToolNames.Select(static name => TextExpr(name)));
        sql.Append(CultureInfo.InvariantCulture, $"WHERE EXISTS (SELECT 1 FROM record_session_link rsl WHERE rsl.span_id = tc.span_id AND rsl.session_key = {TextExpr(sessionKey)}) ");
        sql.Append("AND (");
        sql.Append("((tcm.server_name IS NOT NULL OR tcm.server_tool_name IS NOT NULL) ");
        sql.Append("AND LOWER(REPLACE(COALESCE(tcm.server_name, ''), '_', '-')) = 'kw-docs' ");
        sql.Append("AND tcm.server_tool_name = 'load_skill') ");
        sql.Append(CultureInfo.InvariantCulture, $"OR (tcm.server_name IS NULL AND tcm.server_tool_name IS NULL AND tc.tool_name IN ({unsplitNames}))");
        sql.Append(')');
    }

    /// <inheritdoc />
    public IReadOnlyList<StoredRequestPromptPreview> RequestPromptPreviews(string sessionKey, IReadOnlyCollection<string> requestSpanIds)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionKey);
        ArgumentNullException.ThrowIfNull(requestSpanIds);
        string[] requested = [.. requestSpanIds.Distinct(StringComparer.Ordinal)];
        if (requested.Length == 0)
        {
            return [];
        }

        StringBuilder ctes = new("WITH RECURSIVE ");
        AppendSessionSectionCtes(ctes, TextExpr(sessionKey));
        ctes.Append("requested(request_span_id) AS (SELECT ");
        ctes.Append(string.Join(" UNION ALL SELECT ", requested.Select(static id => TextExpr(id))));
        ctes.Append("), ");
        ctes.Append("subtree(root, span) AS (SELECT request_span_id, request_span_id FROM requested UNION SELECT s.root, ch.span_id FROM subtree s JOIN record_parent ch ON ch.parent_span_id = s.span AND ch.span_id IN (SELECT span_id FROM spans)), ");
        ctes.Append("candidate(root, span, ord) AS (SELECT root, span, 0 FROM subtree WHERE span = root UNION SELECT s.root, t.span_id, 1 + t.position FROM subtree s JOIN turn_entries t ON t.span_id = s.span WHERE t.span_id <> s.root) ");

        StringBuilder sql = new(TabReadScriptHeader);
        sql.Append(ctes);
        sql.Append("SELECT 'PROMPT', hex(CAST(c.root AS BLOB)), hex(CAST(cp.span_id AS BLOB)), c.ord, cp.position, ");
        sql.Append("hex(CAST(cp.bucket AS BLOB)), typeof(cpt.part_text), COALESCE(hex(CAST(cpt.part_text AS BLOB)), '') ");
        sql.Append("FROM candidate c ");
        sql.Append("JOIN content_part cp ON cp.span_id = c.span ");
        sql.Append("LEFT JOIN content_part_text cpt ON cpt.span_id = cp.span_id AND cpt.position = cp.position ");
        sql.Append("ORDER BY c.root ASC, c.ord ASC, cp.position ASC;\n");

        string output = ExecuteSqlite(sql.ToString());
        List<(string Root, int Ord, string Span, int PartPosition, ContentBucket Bucket, string? PlainText)> partRows = [];
        foreach (string line in TabLines(output))
        {
            string[] cols = SplitTabLine(line, 8, "request prompt parts");
            partRows.Add((
                ParseHexRequired(cols[1], "content_part request root"),
                ParseInt32Required(cols[3], "content_part candidate order"),
                ParseHexRequired(cols[2], "content_part.span_id"),
                ParseInt32Required(cols[4], "content_part.position"),
                ParseContentBucket(ParseHexRequired(cols[5], "content_part.bucket")),
                ParseNullableHex(cols[6], cols[7], "content_part_text.part_text")));
        }

        List<StoredRequestPromptPreview> previews = new(requested.Length);
        foreach (string root in requested)
        {
            previews.Add(new StoredRequestPromptPreview(root, ExtractRequestPreviewFromRows(root, partRows)));
        }

        return previews;
    }

    /// <summary>
    /// Resolves one request root's preview from the ordered part rows its subtree's candidates
    /// produced: the request's own prompt-bucket parts first, then its owned turns' in turn order,
    /// with the first candidate carrying a prompt-bucket part deciding — the same rule the detail
    /// figures' preview extraction applies. The rows carry only the two fields that rule reads,
    /// so no content part is materialized.
    /// </summary>
    /// <param name="root">Request span the preview answers.</param>
    /// <param name="partRows">The read's ordered part rows across every requested root.</param>
    /// <returns>The truncated preview, or <see langword="null"/> when no candidate carries one.</returns>
    private static string? ExtractRequestPreviewFromRows(
        string root,
        List<(string Root, int Ord, string Span, int PartPosition, ContentBucket Bucket, string? PlainText)> partRows)
    {
        foreach (IGrouping<(int Ord, string Span), (string Root, int Ord, string Span, int PartPosition, ContentBucket Bucket, string? PlainText)> group in partRows
            .Where(row => string.Equals(row.Root, root, StringComparison.Ordinal))
            .GroupBy(static row => (row.Ord, row.Span))
            .OrderBy(static group => group.Key.Ord))
        {
            if (group.Any(static row => row.Bucket is ContentBucket.UserPrompt or ContentBucket.UserMessage or ContentBucket.ConversationHistory))
            {
                return ExtractReaderPreview(group);
            }
        }

        return null;
    }

    /// <summary>Extracts one candidate record's preview from its part rows, the rule <see cref="ExtractReaderPromptFromParts"/> applies.</summary>
    /// <param name="parts">The candidate's part rows, in part order.</param>
    /// <returns>The truncated preview, or <see langword="null"/> when the prompt parts carry none.</returns>
    private static string? ExtractReaderPreview(
        IEnumerable<(string Root, int Ord, string Span, int PartPosition, ContentBucket Bucket, string? PlainText)> parts)
    {
        ContentBucket[] priorityBuckets =
        [
            ContentBucket.UserPrompt,
            ContentBucket.UserMessage,
            ContentBucket.ConversationHistory,
        ];

        foreach (ContentBucket targetBucket in priorityBuckets)
        {
            foreach ((string Root, int Ord, string Span, int PartPosition, ContentBucket Bucket, string? Text) part in parts
                .Where(row => row.Bucket == targetBucket)
                .OrderBy(static row => row.PartPosition))
            {
                if (part.Text is null)
                {
                    return null;
                }

                string trimmed = part.Text.Trim();
                if (trimmed.Length == 0)
                {
                    continue;
                }

                return TruncateReaderSurrogateSafe(trimmed, 200);
            }
        }

        return null;
    }

    /// <summary>Appends the common table expressions the three section reads share, ending in a comma.</summary>
    /// <param name="sql">Script the expressions are appended to, already carrying <c>WITH RECURSIVE </c>.</param>
    /// <param name="keyExpr">The session key bound as one hex literal.</param>
    /// <remarks>
    /// <para>
    /// <c>spans</c> is the session's record spans, <c>roots</c> the ones with no parent, a
    /// self-parent, or a parent outside the session — the request-row fallback — and
    /// <c>turn_entries</c> the session's turns in turn order: the stored <c>session_turn</c> rows
    /// when the session has them, else its <c>llm.invoke</c> records ordered by start and span.
    /// </para>
    /// <para>
    /// The fallback branches gate themselves on the store's own rows, so one expression tree
    /// answers sessions with and without projections: the <c>EXISTS</c>/<c>NOT EXISTS</c>
    /// predicates leave exactly one branch non-empty.
    /// </para>
    /// </remarks>
    private static void AppendSessionSectionCtes(StringBuilder sql, string keyExpr)
    {
        sql.Append(CultureInfo.InvariantCulture, $"spans(span_id) AS (SELECT rsl.span_id FROM record_session_link rsl WHERE rsl.session_key = {keyExpr}), ");
        sql.Append("roots(span_id) AS (SELECT r.span_id FROM record r LEFT JOIN record_parent rp ON rp.span_id = r.span_id WHERE r.span_id IN (SELECT span_id FROM spans) AND (rp.parent_span_id IS NULL OR rp.parent_span_id = r.span_id OR rp.parent_span_id NOT IN (SELECT span_id FROM spans))), ");
        sql.Append("projected_turns(position, span_id) AS (SELECT position, span_id FROM session_turn WHERE session_id = ");
        sql.Append(keyExpr);
        sql.Append("), ");
        sql.Append("fallback_turns(position, span_id) AS (SELECT ROW_NUMBER() OVER (ORDER BY r.started_at_utc ASC, r.span_id ASC), r.span_id FROM record r WHERE r.span_id IN (SELECT span_id FROM spans) AND r.op = 'llm.invoke' AND NOT EXISTS (SELECT 1 FROM session_turn WHERE session_id = ");
        sql.Append(keyExpr);
        sql.Append(")), ");
        sql.Append("turn_entries(position, span_id) AS (SELECT position, span_id FROM projected_turns WHERE EXISTS (SELECT 1 FROM session_turn WHERE session_id = ");
        sql.Append(keyExpr);
        sql.Append(") UNION ALL SELECT position, span_id FROM fallback_turns), ");
    }

    /// <summary>
    /// Reads a section read's total when its page came back empty — no rows on the page or none in
    /// the set at all — by counting the set the read's common table expressions define.
    /// </summary>
    /// <param name="ctes">The read's common table expressions, already carrying <c>WITH RECURSIVE</c>.</param>
    /// <param name="setName">The CTE the rows are cut from.</param>
    /// <param name="displayName">Name the parse errors carry.</param>
    /// <returns>The set's row count.</returns>
    private int ReadSessionSectionTotal(StringBuilder ctes, string setName, string displayName)
    {
        StringBuilder countSql = new(TabReadScriptHeader);
        countSql.Append(ctes);
        countSql.Append(CultureInfo.InvariantCulture, $"SELECT 'TOTAL', COUNT(*) FROM {setName};\n");
        string[] countLines = [.. TabLines(ExecuteSqlite(countSql.ToString()))];
        if (countLines.Length != 1)
        {
            throw new StoredDataException($"Expected 1 count row for the {displayName} count, got {countLines.Length.ToString(CultureInfo.InvariantCulture)}.");
        }

        return ParseInt32Required(SplitTabLine(countLines[0], 2, displayName)[1], $"{displayName} total");
    }

    /// <summary>
    /// Appends the set of problem ids attached to one session — by <c>ingest_problem_session</c> or
    /// by one of the session's spans — as one parenthesized select.
    /// </summary>
    /// <param name="sql">Script the select is appended to.</param>
    /// <param name="sessionKey">Canonical session key.</param>
    private static void AppendSessionProblemAttachment(StringBuilder sql, string sessionKey)
    {
        string key = TextExpr(sessionKey);
        sql.Append("SELECT problem_id FROM ingest_problem_session WHERE session_id = ");
        sql.Append(key);
        sql.Append(" UNION SELECT psp.problem_id FROM ingest_problem_span psp JOIN record_session_link rsl ON rsl.span_id = psp.span_id WHERE rsl.session_key = ");
        sql.Append(key);
    }

    /// <inheritdoc />
    public IReadOnlyList<CollectedRecord> SessionRecordsContentFree(string sessionKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionKey);

        Dictionary<string, List<CollectedRecord>> recordsBySession =
            QueryContentFreeRecordsBySession([sessionKey], includeToolMetadata: true);
        return recordsBySession.TryGetValue(sessionKey, out List<CollectedRecord>? records) ? records : [];
    }

    /// <inheritdoc />
    public IReadOnlyList<CollectedRecord> SessionTimelineRecords(string sessionKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionKey);

        // Only the columns BuildTimeline's nodes read, plus the record's source name (decision
        // D11): no trace, end time, token count, reported total, error or correction signal, tool
        // metadata, or content child row (decision D17).
        StringBuilder sql = new(TabReadScriptHeader);
        sql.Append(
            "SELECT 'TREC', hex(CAST(r.span_id AS BLOB)), hex(CAST(r.harness_id AS BLOB)), hex(CAST(r.op AS BLOB)), hex(CAST(r.kind AS BLOB)), " +
            "hex(CAST(r.name AS BLOB)), hex(CAST(r.started_at_utc AS BLOB)), hex(CAST(r.status AS BLOB)), " +
            "typeof(rsl.session_key), COALESCE(hex(CAST(rsl.session_key AS BLOB)), ''), " +
            "typeof(rp.parent_span_id), COALESCE(hex(CAST(rp.parent_span_id AS BLOB)), ''), " +
            "typeof(rm.model_id), COALESCE(hex(CAST(rm.model_id AS BLOB)), ''), " +
            "typeof(rrd.duration_ms), COALESCE(CAST(rrd.duration_ms AS TEXT), ''), " +
            "typeof(rrc.amount), COALESCE(CAST(rrc.amount AS TEXT), ''), " +
            "typeof(rrc.currency), COALESCE(hex(CAST(rrc.currency AS BLOB)), ''), " +
            "typeof(rdcw.declared_context_window), COALESCE(CAST(rdcw.declared_context_window AS TEXT), ''), " +
            RecordJoinIdColumns +
            RecordSourceColumns +
            "FROM record r " +
            RecordSourceJoin +
            RecordJoinIdJoin +
            "LEFT JOIN record_session_link rsl ON rsl.span_id = r.span_id " +
            "LEFT JOIN record_parent rp ON rp.span_id = r.span_id " +
            "LEFT JOIN record_model rm ON rm.span_id = r.span_id " +
            "LEFT JOIN record_reported_duration rrd ON rrd.span_id = r.span_id " +
            "LEFT JOIN record_reported_cost rrc ON rrc.span_id = r.span_id " +
            "LEFT JOIN record_declared_context_window rdcw ON rdcw.span_id = r.span_id " +
            $"WHERE rsl.session_key = {TextExpr(sessionKey)} ORDER BY r.rowid ASC;\n");

        List<CollectedRecord> records = [];
        foreach (string line in TabLines(ExecuteSqlite(sql.ToString())))
        {
            string[] cols = line.Split('\t');
            if (cols.Length != 29)
            {
                throw new StoredDataException($"Expected 29 columns for TREC row, got {cols.Length.ToString(CultureInfo.InvariantCulture)}.");
            }

            string spanId = ParseHexRequired(cols[1], "record.span_id");
            double? costAmount = ParseNullableDouble(cols[16], cols[17], "record_reported_cost.amount");
            string? costCurrency = ParseNullableHex(cols[18], cols[19], "record_reported_cost.currency");
            CollectedCost? reportedCost = costAmount.HasValue && costCurrency is not null
                ? new CollectedCost(costAmount.Value, costCurrency)
                : costAmount.HasValue || costCurrency is not null
                    ? throw new StoredDataException("Partial record_reported_cost row returned.")
                    : null;

            records.Add(new CollectedRecord(
                spanId,
                ParseHexRequired(cols[2], "record.harness_id"),
                ParseRecordOp(ParseHexRequired(cols[3], "record.op")),
                ParseRecordKind(ParseHexRequired(cols[4], "record.kind")),
                ParseHexRequired(cols[5], "record.name"),
                ParseTimestampRequired(ParseHexRequired(cols[6], "record.started_at_utc"), "record.started_at_utc"),
                ParseRecordStatus(ParseHexRequired(cols[7], "record.status")),
                ParseNullableHex(cols[8], cols[9], "record_session_link.session_key"),
                ParseNullableHex(cols[10], cols[11], "record_parent.parent_span_id"),
                TraceId: null,
                ParseNullableHex(cols[12], cols[13], "record_model.model_id"),
                EndedAt: null,
                ParseNullableInt64(cols[14], cols[15], "record_reported_duration.duration_ms"),
                Tokens: [],
                reportedCost,
                Parts: [],
                Tool: null,
                Agent: null,
                SkillsInvoked: [],
                AgentInstanceId: null,
                ToolDefinitions: [],
                new CollectedRecordSignals(ErrorMessage: null, EditFailures: null, CorrectionRule: null, ParseNullableInt64(cols[20], cols[21], "record_declared_context_window.declared_context_window")),
                ReportedInputTokens: null,
                ReportedOutputTokens: null,
                SourceName: ParseRecordSourceName(cols, startIndex: 26),
                ResponseId: ParseNullableHex(cols[22], cols[23], "record_response_id.response_id"),
                RequestId: ParseNullableHex(cols[24], cols[25], "record_request_id.request_id")));
        }

        return records;
    }

    private static IReadOnlyList<ReadTimeTurnSummary> BuildContentFreeTurnSummaries(
        IReadOnlyList<StoredSessionTurn> projectedTurns,
        IReadOnlyList<CollectedRecord> records,
        string harnessId,
        IModelPricingLookup? pricing)
    {
        List<(int Position, CollectedRecord Record)> turnEntries = ResolveSessionTurnEntriesForReader(projectedTurns, records);
        List<ReadTimeTurnSummary> summaries = new(turnEntries.Count);
        foreach ((int position, CollectedRecord turnRecord) in turnEntries)
        {
            summaries.Add(BuildReaderTurnSummary(turnRecord, position, harnessId, pricing, out _, out _));
        }

        return summaries;
    }

    /// <summary>
    /// Derives one session's bounded summary figures, tool residency rows, and request metadata
    /// from content-free record scalars, its stored projection, and ingest diagnostics: token
    /// counts, reported costs, models, timestamps, projection metadata, and tool metadata, with
    /// no content column read.
    /// </summary>
    private static SessionFigureSet BuildSessionFigures(
        CollectedSession session,
        IReadOnlyList<CollectedRecord> records,
        StoredSessionProjection? projection,
        IReadOnlyList<StoredSessionTurn> projectedTurns,
        IReadOnlyList<Problem> storedProblems,
        IModelPricingLookup? pricing)
    {
        string sessionKey = session.SessionKey;
        string harnessId = session.HarnessId;

        HashSet<string> sessionSpanIds = new(StringComparer.Ordinal);
        Dictionary<string, CollectedRecord> recordsBySpanId = new(records.Count, StringComparer.Ordinal);
        foreach (CollectedRecord record in records)
        {
            _ = sessionSpanIds.Add(record.RecordId);
            recordsBySpanId[record.RecordId] = record;
        }

        List<(int Position, CollectedRecord Record)> turnEntries = ResolveSessionTurnEntriesForReader(projectedTurns, records);

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
            ReadTimeTurnSummary turnSummary = BuildReaderTurnSummary(
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
            long? ms = ComputeReaderDurationMs(candidate);
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
            ? AggregateReaderCostBlocks(reportedRecordBlocks, CostBasis.Harness, extraProblems)
            : null;

        // A session whose records carry no tokens is priced from the usage the harness reported
        // for the whole session, per model; records with tokens keep pricing per turn.
        ReportedUsageCost? reportedUsageCost = ReportedSessionUsage.Price(session, records, pricing);
        if (reportedUsageCost is not null)
        {
            extraProblems.AddRange(reportedUsageCost.Problems);
        }

        ReadTimeCostSummary sessionPublishedCost = reportedUsageCost is not null
            ? ToReaderCostSummary(reportedUsageCost.Cost)
            : turnPublishedBlocks.Count > 0
                ? AggregateReaderCostBlocks(turnPublishedBlocks, CostBasis.Published, extraProblems)
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

        ReportedSessionUsageTotals? reportedUsageTotals = ReportedSessionUsage.Resolve(session, records);
        if (reportedUsageTotals is not null)
        {
            nullableTotalInput = reportedUsageTotals.CombinedInput;
            nullableTotalOutput = reportedUsageTotals.Output;
            nullableTotalCacheRead = reportedUsageTotals.CacheRead;
            nullableTotalCacheCreation = reportedUsageTotals.CacheWrite;
        }

        double? cacheHitRatio = nullableTotalInput is > 0 && nullableTotalCacheRead.HasValue
            ? (double)nullableTotalCacheRead.Value / nullableTotalInput.Value
            : null;

        // Cache-creation coverage counts turns; reported session usage has no per-turn figures.
        int? cacheCreationCoverage = reportedUsageTotals is null && nullableTotalInput is > 0 && nullableTotalCacheCreation.HasValue && turnEntries.Count > 0
            ? cacheCreationTurns
            : null;

        // Tool figures come from the metadata tables the content-free record read carries:
        // tool names, servers, definition schema sizes, and outcomes are metadata, while the
        // tool input and result text tables are never selected.
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

        List<ReadTimeSessionToolSummary> tools = BuildReaderToolSummaries(
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
        List<ReadTimeSessionRequestSummary> requests = BuildReaderRequestSummaries(
            requestSpanIds,
            records,
            recordsBySpanId,
            turnEntries);

        ReadTimeSessionCoverage coverage = new(
            SchemaMeasurable: projection?.Schema is not null,
            ContextMeasurable: projection?.Context is not null);

        // Server rows, context buckets, and the unused-schema range all price the stored
        // projection and the content-free tool metadata above, so no content column is read.
        List<ReadTimeSessionServerSummary> servers = BuildReaderServerSummaries(projection, tools);
        ReadTimeContextBucketSummary? firstContextBucket = null;
        ReadTimeContextBucketSummary? lastContextBucket = null;
        if (projection?.Context?.Turns is { Count: > 0 } contextTurns)
        {
            List<StoredSessionContextTurn> orderedContext = [.. contextTurns.OrderBy(static t => t.Position)];
            firstContextBucket = BuildReaderContextBucketSummary(orderedContext[0], turnEntries);
            lastContextBucket = BuildReaderContextBucketSummary(orderedContext[^1], turnEntries);
        }

        UnusedSchemaRange? unusedRange = ResolveReaderUnusedRange(projection, harnessId, models, pricing);

        List<Problem> problems = [.. storedProblems];
        problems.AddRange(extraProblems);

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
                Coverage: coverage,
                Problems: problems,
                SkillInvocations: [],
                ToolRowsCount: tools.Count,
                UnmeasuredInputTurns: unmeasuredInputTurns,
                ReconciliationTotal: reconciliationTotal,
                ReconciliationMismatched: reconciliationMismatched,
                ReportedTotalTokens: reportedUsageTotals?.Total),
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

    /// <summary>
    /// Derives one session's sessions-list figures from its session row, content-free records,
    /// stored projection, and ingest problems. The turn loop walks the same scalar columns the
    /// retired session-aggregate read walked; the fields the sessions list renders are kept and
    /// the content-bearing aggregate fields are deliberately absent.
    /// </summary>
    private static SessionListFigure BuildSessionListFigure(
        CollectedSession session,
        IReadOnlyList<CollectedRecord> records,
        int? projectionRequestCount,
        IReadOnlyList<StoredSessionTurn> projectedTurns,
        IReadOnlyList<Problem> storedProblems,
        IModelPricingLookup? pricing)
    {
        string sessionKey = session.SessionKey;
        string harnessId = session.HarnessId;

        HashSet<string> sessionSpanIds = new(StringComparer.Ordinal);
        foreach (CollectedRecord record in records)
        {
            _ = sessionSpanIds.Add(record.RecordId);
        }

        List<(int Position, CollectedRecord Record)> turnEntries = ResolveSessionTurnEntriesForReader(projectedTurns, records);

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
            ReadTimeTurnSummary turnSummary = BuildReaderTurnSummary(
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
            ? AggregateReaderCostBlocks(reportedRecordBlocks, CostBasis.Harness, extraProblems)
            : null;

        // A session whose records carry no tokens is priced from the usage the harness reported
        // for the whole session, per model; records with tokens keep pricing per turn.
        ReportedUsageCost? reportedUsageCost = ReportedSessionUsage.Price(session, records, pricing);
        if (reportedUsageCost is not null)
        {
            extraProblems.AddRange(reportedUsageCost.Problems);
        }

        ReadTimeCostSummary sessionPublishedCost = reportedUsageCost is not null
            ? ToReaderCostSummary(reportedUsageCost.Cost)
            : turnPublishedBlocks.Count > 0
                ? AggregateReaderCostBlocks(turnPublishedBlocks, CostBasis.Published, extraProblems)
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

        int requestCount = projectionRequestCount is > 0 ? projectionRequestCount.Value : rootRecords.Count;

        ReportedSessionUsageTotals? listUsageTotals = ReportedSessionUsage.Resolve(session, records);
        return new SessionListFigure(
            SessionKey: sessionKey,
            HarnessId: harnessId,
            Repo: session.Workspace?.Repository,
            Branch: session.Workspace?.Branch,
            TurnCount: turnEntries.Count,
            RequestCount: requestCount,
            TotalInput: listUsageTotals is not null ? listUsageTotals.CombinedInput : hasInput ? totalInput : null,
            TotalOutput: listUsageTotals is not null ? listUsageTotals.Output : hasOutput ? totalOutput : null,
            ReportedCost: sessionReportedCost,
            PublishedCost: sessionPublishedCost,
            CostBasisMismatch: basisMismatch,
            Models: models,
            ProblemsCount: storedProblems.Count + extraProblems.Count,
            ReportedTotalTokens: listUsageTotals?.Total);
    }

    private List<Problem> QuerySessionIngestProblems(string sessionKey)
    {
        Dictionary<string, List<Problem>> grouped = QuerySessionIngestProblemsBySession([sessionKey]);
        return grouped.TryGetValue(sessionKey, out List<Problem>? problems) ? problems : [];
    }

    private Dictionary<string, IReadOnlyList<StoredSessionTurn>> QuerySessionTurnsBySession(IReadOnlyList<string> sessionKeys)
    {
        Dictionary<string, IReadOnlyList<StoredSessionTurn>> turnsBySession = new(StringComparer.Ordinal);
        if (sessionKeys.Count == 0)
        {
            return turnsBySession;
        }

        string filter = SessionKeyListFilter(sessionKeys);
        string sql = TabReadScriptHeader +
                     "SELECT hex(CAST(session_id AS BLOB)), position, hex(CAST(span_id AS BLOB)) FROM session_turn " +
                     $"WHERE session_id IN ({filter}) ORDER BY session_id ASC, position ASC;\n";

        string output = ExecuteSqlite(sql);
        Dictionary<string, List<StoredSessionTurn>> grouped = new(StringComparer.Ordinal);
        foreach (string line in TabLines(output))
        {
            string[] cols = SplitTabLine(line, 3, "session_turn");
            string sessionId = ParseHexRequired(cols[0], "session_turn.session_id");
            int position = ParseInt32Required(cols[1], "session_turn.position");
            string spanId = ParseHexRequired(cols[2], "session_turn.span_id");
            if (!grouped.TryGetValue(sessionId, out List<StoredSessionTurn>? turns))
            {
                turns = [];
                grouped[sessionId] = turns;
            }

            turns.Add(new StoredSessionTurn(sessionId, position, spanId));
        }

        foreach (KeyValuePair<string, List<StoredSessionTurn>> entry in grouped)
        {
            turnsBySession[entry.Key] = entry.Value;
        }

        return turnsBySession;
    }

    private Dictionary<string, List<Problem>> QuerySessionIngestProblemsBySession(IReadOnlyList<string> sessionKeys)
    {
        Dictionary<string, List<Problem>> problemsBySession = new(StringComparer.Ordinal);
        if (sessionKeys.Count == 0)
        {
            return problemsBySession;
        }

        string filter = SessionKeyListFilter(sessionKeys);
        string sql = TabReadScriptHeader +
                     "SELECT hex(CAST(att.session_key AS BLOB)), " +
                     "hex(CAST(ip.severity AS BLOB)), hex(CAST(ip.code AS BLOB)), hex(CAST(ip.message AS BLOB)), " +
                     "typeof(ipl.location), COALESCE(hex(CAST(ipl.location AS BLOB)), ''), " +
                     "typeof(ips.span_id), COALESCE(hex(CAST(ips.span_id AS BLOB)), ''), " +
                     "typeof(ipst.stack), COALESCE(hex(CAST(ipst.stack AS BLOB)), '') " +
                     "FROM ingest_problem ip " +
                     "LEFT JOIN ingest_problem_location ipl ON ipl.problem_id = ip.problem_id " +
                     "LEFT JOIN ingest_problem_span ips ON ips.problem_id = ip.problem_id " +
                     "LEFT JOIN ingest_problem_stack ipst ON ipst.problem_id = ip.problem_id " +
                     "JOIN (" +
                     $"SELECT session_id AS session_key, problem_id FROM ingest_problem_session WHERE session_id IN ({filter}) UNION " +
                     $"SELECT rsl.session_key AS session_key, psp.problem_id FROM ingest_problem_span psp JOIN record_session_link rsl ON rsl.span_id = psp.span_id WHERE rsl.session_key IN ({filter})" +
                     ") att ON att.problem_id = ip.problem_id " +
                     "ORDER BY att.session_key ASC, ip.problem_id ASC;\n";

        string output = ExecuteSqlite(sql);
        foreach (string line in TabLines(output))
        {
            string[] cols = SplitTabLine(line, 10, "session ingest_problem");
            string sessionId = ParseHexRequired(cols[0], "ingest_problem session_key");
            ProblemSeverity severity = ParseProblemSeverityValue(ParseHexRequired(cols[1], "ingest_problem.severity"));
            string code = ParseHexRequired(cols[2], "ingest_problem.code");
            string message = ParseHexRequired(cols[3], "ingest_problem.message");
            string? location = ParseNullableHex(cols[4], cols[5], "ingest_problem_location.location")
                ?? ParseNullableHex(cols[6], cols[7], "ingest_problem_span.span_id");
            string? stack = ParseNullableHex(cols[8], cols[9], "ingest_problem_stack.stack");
            if (!problemsBySession.TryGetValue(sessionId, out List<Problem>? problems))
            {
                problems = [];
                problemsBySession[sessionId] = problems;
            }

            problems.Add(new Problem(severity, code, message, location, stack));
        }

        return problemsBySession;
    }

    /// <summary>
    /// Parses one tab-split <c>REC</c> row of a record query into a raw record header — the
    /// shared parse behind both the broad record read and the content-free sessions-list read,
    /// so both derive from the same scalar columns.
    /// </summary>
    /// <param name="cols">Tab-split columns of the <c>REC</c> row.</param>
    private static RawRecordHeader ParseRawRecordHeader(string[] cols)
    {
        if (cols.Length != 45)
        {
            throw new StoredDataException($"Expected 45 columns for REC row, got {cols.Length.ToString(CultureInfo.InvariantCulture)}.");
        }

        string spanId = ParseHexRequired(cols[1], "record.span_id");
        string harnessId = ParseHexRequired(cols[2], "record.harness_id");
        RecordOp op = ParseRecordOp(ParseHexRequired(cols[3], "record.op"));
        RecordKind kind = ParseRecordKind(ParseHexRequired(cols[4], "record.kind"));
        string name = ParseHexRequired(cols[5], "record.name");
        DateTimeOffset startedAt = ParseTimestampRequired(ParseHexRequired(cols[6], "record.started_at_utc"), "record.started_at_utc");
        RecordStatus status = ParseRecordStatus(ParseHexRequired(cols[7], "record.status"));
        string? sessionKey = ParseNullableHex(cols[8], cols[9], "record_session_link.session_key");
        string? parentSpanId = ParseNullableHex(cols[10], cols[11], "record_parent.parent_span_id");
        string? traceId = ParseNullableHex(cols[12], cols[13], "record_trace.trace_id");
        string? model = ParseNullableHex(cols[14], cols[15], "record_model.model_id");
        string? endedRaw = ParseNullableHex(cols[16], cols[17], "record_end.ended_at_utc");
        DateTimeOffset? endedAt = endedRaw is null ? null : ParseTimestampRequired(endedRaw, "record_end.ended_at_utc");
        long? reportedDurationMs = ParseNullableInt64(cols[18], cols[19], "record_reported_duration.duration_ms");
        double? costAmount = ParseNullableDouble(cols[20], cols[21], "record_reported_cost.amount");
        string? costCurrency = ParseNullableHex(cols[22], cols[23], "record_reported_cost.currency");
        CollectedCost? reportedCost = costAmount.HasValue && costCurrency is not null
            ? new CollectedCost(costAmount.Value, costCurrency)
            : costAmount.HasValue || costCurrency is not null
                ? throw new StoredDataException("Partial record_reported_cost row returned.")
                : null;
        string? agentInstanceId = ParseNullableHex(cols[24], cols[25], "record_agent_instance.agent_instance_id");
        string? errMsg = ParseNullableHex(cols[26], cols[27], "record_error.message");
        long? editFailures = ParseNullableInt64(cols[28], cols[29], "record_edit_failure.failure_count");
        string? correctionRule = ParseNullableHex(cols[30], cols[31], "record_correction_rule.correction_rule");
        long? declaredWindow = ParseNullableInt64(cols[32], cols[33], "record_declared_context_window.declared_context_window");
        long? repIn = ParseNullableInt64(cols[34], cols[35], "record_reported_total.input");
        long? repOut = ParseNullableInt64(cols[36], cols[37], "record_reported_total.output");
        string? responseId = ParseNullableHex(cols[38], cols[39], "record_response_id.response_id");
        string? requestId = ParseNullableHex(cols[40], cols[41], "record_request_id.request_id");
        string sourceName = ParseRecordSourceName(cols, startIndex: 42);

        return new RawRecordHeader(
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
            reportedCost,
            agentInstanceId,
            new CollectedRecordSignals(errMsg, editFailures, correctionRule, declaredWindow),
            repIn,
            repOut,
            sourceName,
            responseId,
            requestId);
    }

    /// <summary>
    /// Parses the three <see cref="RecordSourceColumns"/> of a record row into the record's source
    /// name as the projection rules read it: the locator for an OTLP source, or
    /// <see cref="CanonSynth.FileSourcePrefix"/> followed by the source's harness for a file source.
    /// </summary>
    /// <remarks>
    /// The name comes from the stored <c>source</c> row alone, never from record attributes, so a
    /// record's source is exactly the batch source it was written under. The file form uses the
    /// same prefix constant the file synthesizer stamps, which is what the projector's file-source
    /// test reads.
    /// </remarks>
    /// <param name="cols">Tab-split columns of the record row.</param>
    /// <param name="startIndex">Index of the first source column.</param>
    private static string ParseRecordSourceName(string[] cols, int startIndex)
    {
        SourceKind kind = ParseSourceKind(ParseHexRequired(cols[startIndex], "source.source_kind"));
        string harnessId = ParseHexRequired(cols[startIndex + 1], "source.harness_id");
        string locator = ParseHexRequired(cols[startIndex + 2], "source.locator");
        return kind == SourceKind.File ? CanonSynth.FileSourcePrefix + harnessId : locator;
    }

    /// <summary>
    /// Reads every record linked to <paramref name="sessionKeys"/> in one batched, content-free
    /// pass: the same <c>REC</c> and <c>TOK</c> selects the broad record read runs, plus the
    /// tool-definition and tool-call metadata selects when
    /// <paramref name="includeToolMetadata"/> asks for them, with no <c>content_part</c>, tool
    /// input text, tool result text, agent-invocation, or skill select.
    /// </summary>
    /// <param name="sessionKeys">Canonical session keys whose records are read.</param>
    /// <param name="includeToolMetadata">
    /// Whether to read the tool-definition and tool-call metadata rows. The session detail and
    /// summary figures need them; the session list does not, and its payload is answered from
    /// the projection's ranked schema, so its read stays off the tool tables entirely.
    /// </param>
    private Dictionary<string, List<CollectedRecord>> QueryContentFreeRecordsBySession(
        IReadOnlyList<string> sessionKeys,
        bool includeToolMetadata = false,
        string? scopedSpanId = null)
    {
        Dictionary<string, List<CollectedRecord>> recordsBySession = new(StringComparer.Ordinal);
        if (sessionKeys.Count == 0 && scopedSpanId is null)
        {
            return recordsBySession;
        }

        // A span-scoped read filters the record tables by that span alone, so the span inspector
        // pays for one record rather than its session's whole conversation metadata.
        string? spanExpr = scopedSpanId is null ? null : TextExpr(scopedSpanId);
        string keyFilter = SessionKeyListFilter(sessionKeys);
        string recordFilter = spanExpr is null
            ? $" WHERE rsl.session_key IN ({keyFilter})"
            : $" WHERE r.span_id = {spanExpr}";
        string tokenFilter = spanExpr is null
            ? $" WHERE r.span_id IN (SELECT rsl.span_id FROM record_session_link rsl WHERE rsl.session_key IN ({keyFilter}))"
            : $" WHERE r.span_id = {spanExpr}";
        string linkedSpanIds = spanExpr is null
            ? $"(SELECT rsl.span_id FROM record_session_link rsl WHERE rsl.session_key IN ({keyFilter}))"
            : $"({spanExpr})";

        StringBuilder sql = new(TabReadScriptHeader);
        sql.Append(
            "SELECT 'REC', hex(CAST(r.span_id AS BLOB)), hex(CAST(r.harness_id AS BLOB)), hex(CAST(r.op AS BLOB)), hex(CAST(r.kind AS BLOB)), " +
            "hex(CAST(r.name AS BLOB)), hex(CAST(r.started_at_utc AS BLOB)), hex(CAST(r.status AS BLOB)), " +
            "typeof(rsl.session_key), COALESCE(hex(CAST(rsl.session_key AS BLOB)), ''), " +
            "typeof(rp.parent_span_id), COALESCE(hex(CAST(rp.parent_span_id AS BLOB)), ''), " +
            "typeof(rt.trace_id), COALESCE(hex(CAST(rt.trace_id AS BLOB)), ''), " +
            "typeof(rm.model_id), COALESCE(hex(CAST(rm.model_id AS BLOB)), ''), " +
            "typeof(re.ended_at_utc), COALESCE(hex(CAST(re.ended_at_utc AS BLOB)), ''), " +
            "typeof(rrd.duration_ms), COALESCE(CAST(rrd.duration_ms AS TEXT), ''), " +
            "typeof(rrc.amount), COALESCE(CAST(rrc.amount AS TEXT), ''), " +
            "typeof(rrc.currency), COALESCE(hex(CAST(rrc.currency AS BLOB)), ''), " +
            "typeof(rai.agent_instance_id), COALESCE(hex(CAST(rai.agent_instance_id AS BLOB)), ''), " +
            "typeof(rerr.message), COALESCE(hex(CAST(rerr.message AS BLOB)), ''), " +
            "typeof(ref.failure_count), COALESCE(CAST(ref.failure_count AS TEXT), ''), " +
            "typeof(rcr.correction_rule), COALESCE(hex(CAST(rcr.correction_rule AS BLOB)), ''), " +
            "typeof(rdcw.declared_context_window), COALESCE(CAST(rdcw.declared_context_window AS TEXT), ''), " +
            "typeof(rrti.token_count), COALESCE(CAST(rrti.token_count AS TEXT), ''), " +
            "typeof(rrto.token_count), COALESCE(CAST(rrto.token_count AS TEXT), ''), " +
            RecordJoinIdColumns +
            RecordSourceColumns +
            "FROM record r " +
            RecordSourceJoin +
            RecordJoinIdJoin +
            "LEFT JOIN record_session_link rsl ON rsl.span_id = r.span_id " +
            "LEFT JOIN record_parent rp ON rp.span_id = r.span_id " +
            "LEFT JOIN record_trace rt ON rt.span_id = r.span_id " +
            "LEFT JOIN record_model rm ON rm.span_id = r.span_id " +
            "LEFT JOIN record_end re ON re.span_id = r.span_id " +
            "LEFT JOIN record_reported_duration rrd ON rrd.span_id = r.span_id " +
            "LEFT JOIN record_reported_cost rrc ON rrc.span_id = r.span_id " +
            "LEFT JOIN record_agent_instance rai ON rai.span_id = r.span_id " +
            "LEFT JOIN record_error rerr ON rerr.span_id = r.span_id " +
            "LEFT JOIN record_edit_failure ref ON ref.span_id = r.span_id " +
            "LEFT JOIN record_correction_rule rcr ON rcr.span_id = r.span_id " +
            "LEFT JOIN record_declared_context_window rdcw ON rdcw.span_id = r.span_id " +
            "LEFT JOIN record_reported_total rrti ON rrti.span_id = r.span_id AND rrti.direction = 'input' " +
            "LEFT JOIN record_reported_total rrto ON rrto.span_id = r.span_id AND rrto.direction = 'output'" +
            $"{recordFilter} ORDER BY r.rowid ASC;\n");

        sql.Append(
            "SELECT 'TOK', hex(CAST(r.span_id AS BLOB)), hex(CAST(r.token_class AS BLOB)), r.token_count " +
            $"FROM record_token_count r{tokenFilter} ORDER BY r.rowid ASC;\n");

        if (includeToolMetadata)
        {
            sql.Append(
                "SELECT 'TDEF', hex(CAST(td.span_id AS BLOB)), td.position, hex(CAST(td.tool_name AS BLOB)), " +
                "typeof(tds.server_name), COALESCE(hex(CAST(tds.server_name AS BLOB)), ''), td.schema_tokens " +
                "FROM tool_definition td " +
                "LEFT JOIN tool_definition_server tds ON tds.span_id = td.span_id AND tds.position = td.position" +
                $" WHERE td.span_id IN {linkedSpanIds} ORDER BY td.span_id ASC, td.position ASC;\n");

            // The tool-call metadata joins deliberately omit tool_call_input_text and
            // tool_call_result_text: sizes, fingerprints, outcomes, errors, and exit codes are
            // metadata, and those two tables hold the only tool content.
            sql.Append("SELECT 'TC', hex(CAST(tc.span_id AS BLOB)), ");
            sql.Append(BuildToolCallMetadataColumns("tc"));
            sql.Append(" FROM tool_call tc " +
                       "LEFT JOIN tool_call_mcp tcm ON tcm.span_id = tc.span_id " +
                       "LEFT JOIN tool_call_input_size tcis ON tcis.span_id = tc.span_id " +
                       "LEFT JOIN tool_call_input_fingerprint tcif ON tcif.span_id = tc.span_id " +
                       "LEFT JOIN tool_call_input_truncation tcitrc ON tcitrc.span_id = tc.span_id " +
                       "LEFT JOIN tool_call_result_size tcrs ON tcrs.span_id = tc.span_id " +
                       "LEFT JOIN tool_call_result_truncation tcrtrc ON tcrtrc.span_id = tc.span_id " +
                       "LEFT JOIN tool_call_outcome tco ON tco.span_id = tc.span_id " +
                       "LEFT JOIN tool_call_error tce ON tce.span_id = tc.span_id " +
                       "LEFT JOIN tool_call_error_code tcec ON tcec.span_id = tc.span_id " +
                       "LEFT JOIN tool_call_exit_code tcex ON tcex.span_id = tc.span_id" +
                       $" WHERE tc.span_id IN {linkedSpanIds} ORDER BY tc.rowid ASC;\n");
        }

        string output = ExecuteSqlite(sql.ToString());
        List<RawRecordHeader> rawRecords = [];
        Dictionary<string, List<CollectedTokenCount>> tokensBySpan = new(StringComparer.Ordinal);
        Dictionary<string, List<CollectedToolDefinition>> defsBySpan = new(StringComparer.Ordinal);
        Dictionary<string, CollectedToolCall> toolCallsBySpan = new(StringComparer.Ordinal);

        foreach (string line in TabLines(output))
        {
            string[] cols = line.Split('\t');
            if (cols.Length < 1)
            {
                throw new StoredDataException("Unexpected empty row in QueryContentFreeRecordsBySession.");
            }

            switch (cols[0])
            {
                case "REC":
                    rawRecords.Add(ParseRawRecordHeader(cols));
                    break;

                case "TOK":
                    {
                        if (cols.Length != 4)
                        {
                            throw new StoredDataException($"Expected 4 columns for TOK row, got {cols.Length.ToString(CultureInfo.InvariantCulture)}.");
                        }

                        string spanId = ParseHexRequired(cols[1], "record_token_count.span_id");
                        TokenClass tokenClass = ParseTokenClass(ParseHexRequired(cols[2], "record_token_count.token_class"));
                        long count = ParseInt64Required(cols[3], "record_token_count.token_count");
                        GetOrCreateList(tokensBySpan, spanId).Add(new CollectedTokenCount(tokenClass, count));
                        break;
                    }

                case "TDEF":
                    {
                        if (cols.Length != 7)
                        {
                            throw new StoredDataException($"Expected 7 columns for TDEF row, got {cols.Length.ToString(CultureInfo.InvariantCulture)}.");
                        }

                        string spanId = ParseHexRequired(cols[1], "tool_definition.span_id");
                        int position = ParseInt32Required(cols[2], "tool_definition.position");
                        string toolName = ParseHexRequired(cols[3], "tool_definition.tool_name");
                        string? server = ParseNullableHex(cols[4], cols[5], "tool_definition_server.server_name");
                        long schemaTokens = ParseInt64Required(cols[6], "tool_definition.schema_tokens");
                        GetOrCreateList(defsBySpan, spanId).Add(new CollectedToolDefinition(position, toolName, server, schemaTokens));
                        break;
                    }

                case "TC":
                    {
                        if (cols.Length != 34)
                        {
                            throw new StoredDataException($"Expected 34 columns for TC row, got {cols.Length.ToString(CultureInfo.InvariantCulture)}.");
                        }

                        string spanId = ParseHexRequired(cols[1], "tool_call.span_id");
                        toolCallsBySpan[spanId] = ParseToolCallColumns(cols, startIndex: 2);
                        break;
                    }

                default:
                    throw new StoredDataException($"Unexpected content-free record query row tag '{cols[0]}'.");
            }
        }

        foreach (RawRecordHeader header in rawRecords)
        {
            CollectedRecord record = new(
                header.SpanId,
                header.HarnessId,
                header.Op,
                header.Kind,
                header.Name,
                header.StartedAt,
                header.Status,
                header.SessionKey,
                header.ParentSpanId,
                header.TraceId,
                header.Model,
                header.EndedAt,
                header.ReportedDurationMs,
                tokensBySpan.TryGetValue(header.SpanId, out List<CollectedTokenCount>? tokens) ? tokens : [],
                header.ReportedCost,
                Parts: [],
                Tool: toolCallsBySpan.GetValueOrDefault(header.SpanId),
                Agent: null,
                SkillsInvoked: [],
                header.AgentInstanceId,
                defsBySpan.TryGetValue(header.SpanId, out List<CollectedToolDefinition>? defs) ? defs : [],
                header.Signals,
                header.ReportedInputTokens,
                header.ReportedOutputTokens,
                header.SourceName,
                header.ResponseId,
                header.RequestId);
            // A record with no session link is grouped under the empty key rather than dropped,
            // so a span-scoped read still answers it. A session key is never empty, so the
            // session-scoped callers are unaffected.
            string groupingKey = record.SessionKey ?? string.Empty;
            if (!recordsBySession.TryGetValue(groupingKey, out List<CollectedRecord>? sessionRecords))
            {
                sessionRecords = [];
                recordsBySession[groupingKey] = sessionRecords;
            }

            sessionRecords.Add(record);
        }

        return recordsBySession;
    }

    private IReadOnlyList<CollectedRecord> QueryRecords(string? wherePredicate)
    {
        string recordFilter = wherePredicate is null ? string.Empty : $" WHERE {wherePredicate}";
        string childSpanFilter = wherePredicate is null
            ? string.Empty
            : $" WHERE r.span_id IN (SELECT r.span_id FROM record r WHERE {wherePredicate})";
        string partFilter = wherePredicate is null
            ? string.Empty
            : $" WHERE cp.span_id IN (SELECT r.span_id FROM record r WHERE {wherePredicate})";
        string defFilter = wherePredicate is null
            ? string.Empty
            : $" WHERE td.span_id IN (SELECT r.span_id FROM record r WHERE {wherePredicate})";
        string tcFilter = wherePredicate is null
            ? string.Empty
            : $" WHERE tc.span_id IN (SELECT r.span_id FROM record r WHERE {wherePredicate})";
        string aiFilter = wherePredicate is null
            ? string.Empty
            : $" WHERE ai.span_id IN (SELECT r.span_id FROM record r WHERE {wherePredicate})";

        StringBuilder sql = new(TabReadScriptHeader);
        sql.Append(
            "SELECT 'REC', hex(CAST(r.span_id AS BLOB)), hex(CAST(r.harness_id AS BLOB)), hex(CAST(r.op AS BLOB)), hex(CAST(r.kind AS BLOB)), " +
            "hex(CAST(r.name AS BLOB)), hex(CAST(r.started_at_utc AS BLOB)), hex(CAST(r.status AS BLOB)), " +
            "typeof(rsl.session_key), COALESCE(hex(CAST(rsl.session_key AS BLOB)), ''), " +
            "typeof(rp.parent_span_id), COALESCE(hex(CAST(rp.parent_span_id AS BLOB)), ''), " +
            "typeof(rt.trace_id), COALESCE(hex(CAST(rt.trace_id AS BLOB)), ''), " +
            "typeof(rm.model_id), COALESCE(hex(CAST(rm.model_id AS BLOB)), ''), " +
            "typeof(re.ended_at_utc), COALESCE(hex(CAST(re.ended_at_utc AS BLOB)), ''), " +
            "typeof(rrd.duration_ms), COALESCE(CAST(rrd.duration_ms AS TEXT), ''), " +
            "typeof(rrc.amount), COALESCE(CAST(rrc.amount AS TEXT), ''), " +
            "typeof(rrc.currency), COALESCE(hex(CAST(rrc.currency AS BLOB)), ''), " +
            "typeof(rai.agent_instance_id), COALESCE(hex(CAST(rai.agent_instance_id AS BLOB)), ''), " +
            "typeof(rerr.message), COALESCE(hex(CAST(rerr.message AS BLOB)), ''), " +
            "typeof(ref.failure_count), COALESCE(CAST(ref.failure_count AS TEXT), ''), " +
            "typeof(rcr.correction_rule), COALESCE(hex(CAST(rcr.correction_rule AS BLOB)), ''), " +
            "typeof(rdcw.declared_context_window), COALESCE(CAST(rdcw.declared_context_window AS TEXT), ''), " +
            "typeof(rrti.token_count), COALESCE(CAST(rrti.token_count AS TEXT), ''), " +
            "typeof(rrto.token_count), COALESCE(CAST(rrto.token_count AS TEXT), ''), " +
            RecordJoinIdColumns +
            RecordSourceColumns +
            "FROM record r " +
            RecordSourceJoin +
            RecordJoinIdJoin +
            "LEFT JOIN record_session_link rsl ON rsl.span_id = r.span_id " +
            "LEFT JOIN record_parent rp ON rp.span_id = r.span_id " +
            "LEFT JOIN record_trace rt ON rt.span_id = r.span_id " +
            "LEFT JOIN record_model rm ON rm.span_id = r.span_id " +
            "LEFT JOIN record_end re ON re.span_id = r.span_id " +
            "LEFT JOIN record_reported_duration rrd ON rrd.span_id = r.span_id " +
            "LEFT JOIN record_reported_cost rrc ON rrc.span_id = r.span_id " +
            "LEFT JOIN record_agent_instance rai ON rai.span_id = r.span_id " +
            "LEFT JOIN record_error rerr ON rerr.span_id = r.span_id " +
            "LEFT JOIN record_edit_failure ref ON ref.span_id = r.span_id " +
            "LEFT JOIN record_correction_rule rcr ON rcr.span_id = r.span_id " +
            "LEFT JOIN record_declared_context_window rdcw ON rdcw.span_id = r.span_id " +
            "LEFT JOIN record_reported_total rrti ON rrti.span_id = r.span_id AND rrti.direction = 'input' " +
            "LEFT JOIN record_reported_total rrto ON rrto.span_id = r.span_id AND rrto.direction = 'output'" +
            $"{recordFilter} ORDER BY r.rowid ASC;\n");

        sql.Append(
            "SELECT 'TOK', hex(CAST(r.span_id AS BLOB)), hex(CAST(r.token_class AS BLOB)), r.token_count " +
            $"FROM record_token_count r{childSpanFilter} ORDER BY r.rowid ASC;\n");

        sql.Append(
            "SELECT 'SKL', hex(CAST(r.span_id AS BLOB)), hex(CAST(r.skill_name AS BLOB)) " +
            $"FROM record_skill_invocation r{childSpanFilter} ORDER BY r.rowid ASC;\n");

        sql.Append("SELECT 'PART', hex(CAST(cp.span_id AS BLOB)), ");
        sql.Append(BuildContentPartColumns("cp"));
        sql.Append(" FROM content_part cp " +
                   "LEFT JOIN content_part_text cpt ON cpt.span_id = cp.span_id AND cpt.position = cp.position " +
                   "LEFT JOIN content_part_withheld cpw ON cpw.span_id = cp.span_id AND cpw.position = cp.position " +
                   "LEFT JOIN content_part_reported_tokens cprt ON cprt.span_id = cp.span_id AND cprt.position = cp.position " +
                   "LEFT JOIN content_part_server cps ON cps.span_id = cp.span_id AND cps.position = cp.position " +
                   "LEFT JOIN content_part_role cpr ON cpr.span_id = cp.span_id AND cpr.position = cp.position " +
                   "LEFT JOIN content_part_residency cpres ON cpres.span_id = cp.span_id AND cpres.position = cp.position " +
                   "LEFT JOIN content_part_truncation cptr ON cptr.span_id = cp.span_id AND cptr.position = cp.position" +
                   $"{partFilter} ORDER BY cp.span_id ASC, cp.position ASC;\n");

        sql.Append(
            "SELECT 'TDEF', hex(CAST(td.span_id AS BLOB)), td.position, hex(CAST(td.tool_name AS BLOB)), " +
            "typeof(tds.server_name), COALESCE(hex(CAST(tds.server_name AS BLOB)), ''), td.schema_tokens " +
            "FROM tool_definition td " +
            "LEFT JOIN tool_definition_server tds ON tds.span_id = td.span_id AND tds.position = td.position" +
            $"{defFilter} ORDER BY td.span_id ASC, td.position ASC;\n");

        sql.Append("SELECT 'TC', hex(CAST(tc.span_id AS BLOB)), ");
        sql.Append(BuildToolCallColumns("tc"));
        sql.Append(" FROM tool_call tc " +
                   "LEFT JOIN tool_call_mcp tcm ON tcm.span_id = tc.span_id " +
                   "LEFT JOIN tool_call_input_size tcis ON tcis.span_id = tc.span_id " +
                   "LEFT JOIN tool_call_input_text tcit ON tcit.span_id = tc.span_id " +
                   "LEFT JOIN tool_call_input_fingerprint tcif ON tcif.span_id = tc.span_id " +
                   "LEFT JOIN tool_call_input_truncation tcitrc ON tcitrc.span_id = tc.span_id " +
                   "LEFT JOIN tool_call_result_size tcrs ON tcrs.span_id = tc.span_id " +
                   "LEFT JOIN tool_call_result_text tcrt ON tcrt.span_id = tc.span_id " +
                   "LEFT JOIN tool_call_result_truncation tcrtrc ON tcrtrc.span_id = tc.span_id " +
                   "LEFT JOIN tool_call_outcome tco ON tco.span_id = tc.span_id " +
                   "LEFT JOIN tool_call_error tce ON tce.span_id = tc.span_id " +
                   "LEFT JOIN tool_call_error_code tcec ON tcec.span_id = tc.span_id " +
                   "LEFT JOIN tool_call_exit_code tcex ON tcex.span_id = tc.span_id" +
                   $"{tcFilter} ORDER BY tc.rowid ASC;\n");

        sql.Append("SELECT 'AI', hex(CAST(ai.span_id AS BLOB)), ");
        sql.Append(BuildAgentInvocationColumns("ai"));
        sql.Append(" FROM agent_invocation ai " +
                   "LEFT JOIN agent_invocation_spawn ais ON ais.span_id = ai.span_id " +
                   "LEFT JOIN agent_invocation_outcome aio ON aio.span_id = ai.span_id " +
                   "LEFT JOIN agent_invocation_error aie ON aie.span_id = ai.span_id" +
                   $"{aiFilter} ORDER BY ai.rowid ASC;\n");

        string output = ExecuteSqlite(sql.ToString());
        List<RawRecordHeader> rawRecords = [];
        Dictionary<string, List<CollectedTokenCount>> tokensBySpan = new(StringComparer.Ordinal);
        Dictionary<string, List<string>> skillsBySpan = new(StringComparer.Ordinal);
        Dictionary<string, List<CollectedContentPart>> partsBySpan = new(StringComparer.Ordinal);
        Dictionary<string, List<CollectedToolDefinition>> defsBySpan = new(StringComparer.Ordinal);
        Dictionary<string, CollectedToolCall> toolCallsBySpan = new(StringComparer.Ordinal);
        Dictionary<string, CollectedAgentInvocation> agentCallsBySpan = new(StringComparer.Ordinal);

        foreach (string line in TabLines(output))
        {
            string[] cols = line.Split('\t');
            if (cols.Length < 1)
            {
                throw new StoredDataException("Unexpected empty row in QueryRecords.");
            }

            switch (cols[0])
            {
                case "REC":
                    {
                        rawRecords.Add(ParseRawRecordHeader(cols));
                        break;
                    }

                case "TOK":
                    {
                        if (cols.Length != 4)
                        {
                            throw new StoredDataException($"Expected 4 columns for TOK row, got {cols.Length.ToString(CultureInfo.InvariantCulture)}.");
                        }

                        string spanId = ParseHexRequired(cols[1], "record_token_count.span_id");
                        TokenClass tokenClass = ParseTokenClass(ParseHexRequired(cols[2], "record_token_count.token_class"));
                        long count = ParseInt64Required(cols[3], "record_token_count.token_count");
                        GetOrCreateList(tokensBySpan, spanId).Add(new CollectedTokenCount(tokenClass, count));
                        break;
                    }

                case "SKL":
                    {
                        if (cols.Length != 3)
                        {
                            throw new StoredDataException($"Expected 3 columns for SKL row, got {cols.Length.ToString(CultureInfo.InvariantCulture)}.");
                        }

                        string spanId = ParseHexRequired(cols[1], "record_skill_invocation.span_id");
                        string skill = ParseHexRequired(cols[2], "record_skill_invocation.skill_name");
                        GetOrCreateList(skillsBySpan, spanId).Add(skill);
                        break;
                    }

                case "PART":
                    {
                        if (cols.Length != 22)
                        {
                            throw new StoredDataException($"Expected 22 columns for PART row, got {cols.Length.ToString(CultureInfo.InvariantCulture)}.");
                        }

                        string spanId = ParseHexRequired(cols[1], "content_part.span_id");
                        CollectedContentPart part = ParseContentPartColumns(cols, startIndex: 2);
                        GetOrCreateList(partsBySpan, spanId).Add(part);
                        break;
                    }

                case "TDEF":
                    {
                        if (cols.Length != 7)
                        {
                            throw new StoredDataException($"Expected 7 columns for TDEF row, got {cols.Length.ToString(CultureInfo.InvariantCulture)}.");
                        }

                        string spanId = ParseHexRequired(cols[1], "tool_definition.span_id");
                        int position = ParseInt32Required(cols[2], "tool_definition.position");
                        string toolName = ParseHexRequired(cols[3], "tool_definition.tool_name");
                        string? server = ParseNullableHex(cols[4], cols[5], "tool_definition_server.server_name");
                        long schemaTokens = ParseInt64Required(cols[6], "tool_definition.schema_tokens");
                        GetOrCreateList(defsBySpan, spanId).Add(new CollectedToolDefinition(position, toolName, server, schemaTokens));
                        break;
                    }

                case "TC":
                    {
                        if (cols.Length != 34)
                        {
                            throw new StoredDataException($"Expected 34 columns for TC row, got {cols.Length.ToString(CultureInfo.InvariantCulture)}.");
                        }

                        string spanId = ParseHexRequired(cols[1], "tool_call.span_id");
                        toolCallsBySpan[spanId] = ParseToolCallColumns(cols, startIndex: 2);
                        break;
                    }

                case "AI":
                    {
                        if (cols.Length != 9)
                        {
                            throw new StoredDataException($"Expected 9 columns for AI row, got {cols.Length.ToString(CultureInfo.InvariantCulture)}.");
                        }

                        string spanId = ParseHexRequired(cols[1], "agent_invocation.span_id");
                        agentCallsBySpan[spanId] = ParseAgentInvocationColumns(cols, startIndex: 2);
                        break;
                    }

                default:
                    throw new StoredDataException($"Unexpected record query row tag '{cols[0]}'.");
            }
        }

        List<CollectedRecord> materialized = new(rawRecords.Count);
        foreach (RawRecordHeader header in rawRecords)
        {
            materialized.Add(new CollectedRecord(
                header.SpanId,
                header.HarnessId,
                header.Op,
                header.Kind,
                header.Name,
                header.StartedAt,
                header.Status,
                header.SessionKey,
                header.ParentSpanId,
                header.TraceId,
                header.Model,
                header.EndedAt,
                header.ReportedDurationMs,
                tokensBySpan.TryGetValue(header.SpanId, out List<CollectedTokenCount>? tok) ? tok : [],
                header.ReportedCost,
                partsBySpan.TryGetValue(header.SpanId, out List<CollectedContentPart>? prt) ? prt : [],
                toolCallsBySpan.GetValueOrDefault(header.SpanId),
                agentCallsBySpan.GetValueOrDefault(header.SpanId),
                skillsBySpan.TryGetValue(header.SpanId, out List<string>? skl) ? skl : [],
                header.AgentInstanceId,
                defsBySpan.TryGetValue(header.SpanId, out List<CollectedToolDefinition>? def) ? def : [],
                header.Signals,
                header.ReportedInputTokens,
                header.ReportedOutputTokens,
                header.SourceName,
                header.ResponseId,
                header.RequestId));
        }

        return materialized;
    }

    private IReadOnlyList<CollectedSession> QuerySessions(string? sessionPredicate, string? usagePredicate)
    {
        string sessionFilter = sessionPredicate is null ? string.Empty : $" WHERE {sessionPredicate}";
        string usageFilter = usagePredicate is null ? string.Empty : $" WHERE {usagePredicate}";

        string sql = TabReadScriptHeader +
                     "SELECT 'SESS', hex(CAST(s.session_id AS BLOB)), hex(CAST(s.harness_id AS BLOB)), hex(CAST(s.native_session_id AS BLOB)), " +
                     "typeof(sw.working_directory), COALESCE(hex(CAST(sw.working_directory AS BLOB)), ''), " +
                     "typeof(sr.repo_name), COALESCE(hex(CAST(sr.repo_name AS BLOB)), ''), " +
                     "typeof(sb.branch_name), COALESCE(hex(CAST(sb.branch_name AS BLOB)), ''), " +
                     "typeof(st.title), COALESCE(hex(CAST(st.title AS BLOB)), '') " +
                     "FROM session s " +
                     "LEFT JOIN session_workspace sw ON sw.session_id = s.session_id " +
                     "LEFT JOIN session_repository sr ON sr.session_id = s.session_id " +
                     "LEFT JOIN session_branch sb ON sb.session_id = s.session_id " +
                     "LEFT JOIN session_title st ON st.session_id = s.session_id" +
                     $"{sessionFilter} ORDER BY s.rowid ASC;\n" +
                     "SELECT 'USAGE', hex(CAST(u.session_id AS BLOB)), u.snapshot_position, hex(CAST(u.model_id AS BLOB)), hex(CAST(u.token_class AS BLOB)), u.token_count " +
                     $"FROM session_model_usage u{usageFilter} ORDER BY u.session_id ASC, u.snapshot_position ASC, u.model_id ASC, u.token_class ASC;\n";

        string output = ExecuteSqlite(sql);
        List<RawSessionHeader> headers = [];
        Dictionary<string, List<CollectedModelUsage>> usagesBySession = new(StringComparer.Ordinal);

        foreach (string line in TabLines(output))
        {
            string[] cols = line.Split('\t');
            if (cols.Length < 1)
            {
                throw new StoredDataException("Unexpected empty row in QuerySessions.");
            }

            switch (cols[0])
            {
                case "SESS":
                    {
                        if (cols.Length != 12)
                        {
                            throw new StoredDataException($"Expected 12 columns for SESS row, got {cols.Length.ToString(CultureInfo.InvariantCulture)}.");
                        }

                        string sessionId = ParseHexRequired(cols[1], "session.session_id");
                        string harnessId = ParseHexRequired(cols[2], "session.harness_id");
                        string nativeId = ParseHexRequired(cols[3], "session.native_session_id");
                        string? workingDir = ParseNullableHex(cols[4], cols[5], "session_workspace.working_directory");
                        string? repo = ParseNullableHex(cols[6], cols[7], "session_repository.repo_name");
                        string? branch = ParseNullableHex(cols[8], cols[9], "session_branch.branch_name");
                        string? title = ParseNullableHex(cols[10], cols[11], "session_title.title");
                        CollectedWorkspace? workspace = workingDir is null && repo is null && branch is null
                            ? null
                            : new CollectedWorkspace(workingDir, repo, branch);
                        headers.Add(new RawSessionHeader(sessionId, harnessId, nativeId, workspace, title));
                        break;
                    }

                case "USAGE":
                    {
                        if (cols.Length != 6)
                        {
                            throw new StoredDataException($"Expected 6 columns for USAGE row, got {cols.Length.ToString(CultureInfo.InvariantCulture)}.");
                        }

                        string sessionId = ParseHexRequired(cols[1], "session_model_usage.session_id");
                        int position = ParseInt32Required(cols[2], "session_model_usage.snapshot_position");
                        string modelId = ParseHexRequired(cols[3], "session_model_usage.model_id");
                        TokenClass tokenClass = ParseTokenClass(ParseHexRequired(cols[4], "session_model_usage.token_class"));
                        long count = ParseInt64Required(cols[5], "session_model_usage.token_count");
                        GetOrCreateList(usagesBySession, sessionId).Add(new CollectedModelUsage(position, modelId, tokenClass, count));
                        break;
                    }

                default:
                    throw new StoredDataException($"Unexpected session query row tag '{cols[0]}'.");
            }
        }

        List<CollectedSession> sessions = new(headers.Count);
        foreach (RawSessionHeader header in headers)
        {
            sessions.Add(new CollectedSession(
                header.SessionId,
                header.HarnessId,
                header.NativeId,
                header.Workspace,
                header.Title,
                usagesBySession.TryGetValue(header.SessionId, out List<CollectedModelUsage>? usages) ? usages : []));
        }

        return sessions;
    }

    private IReadOnlyList<StoredSessionProjection> QuerySessionProjections(string? filterPredicate)
    {
        string projWhere = filterPredicate is null ? string.Empty : $" WHERE {filterPredicate}";
        string subSelect = filterPredicate is null
            ? "SELECT session_id FROM session_projection"
            : $"SELECT sp.session_id FROM session_projection sp WHERE {filterPredicate}";
        string childWhere = filterPredicate is null ? string.Empty : $" WHERE session_id IN ({subSelect})";
        string childAliasedWhere = filterPredicate is null ? string.Empty : $" WHERE t.session_id IN ({subSelect})";

        StringBuilder sql = new(TabReadScriptHeader);
        sql.Append(
            "SELECT 'PROJ', " + SessionProjectionHeaderColumns + " " +
            "FROM session_projection sp " + SessionProjectionHeaderJoins +
            $"{projWhere} ORDER BY sp.started_at_utc DESC, sp.rowid ASC;\n");

        sql.Append(
            CultureInfo.InvariantCulture,
            $"SELECT 'REQ', hex(CAST(session_id AS BLOB)), hex(CAST(request_span_id AS BLOB)) FROM session_request{childWhere} ORDER BY session_id ASC, position ASC;\n");

        sql.Append(
            CultureInfo.InvariantCulture,
            $"SELECT 'TURN', hex(CAST(session_id AS BLOB)), position, hex(CAST(span_id AS BLOB)) FROM session_turn{childWhere} ORDER BY session_id ASC, position ASC;\n");

        sql.Append(
            CultureInfo.InvariantCulture,
            $"SELECT 'CTX', hex(CAST(session_id AS BLOB)), context_limit, hex(CAST(context_limit_source AS BLOB)), residual_total, fresh_jump_factor, accumulation_rate FROM session_context{childWhere};\n");

        sql.Append(
            CultureInfo.InvariantCulture,
            $"SELECT 'CTX_OBS', hex(CAST(session_id AS BLOB)), hex(CAST(bucket AS BLOB)) FROM session_context_observed_part{childWhere} ORDER BY session_id ASC, rowid ASC;\n");

        sql.Append(
            CultureInfo.InvariantCulture,
            $"SELECT 'CTX_DER', hex(CAST(session_id AS BLOB)), hex(CAST(counter AS BLOB)), count FROM session_context_derived_count{childWhere} ORDER BY session_id ASC, rowid ASC;\n");

        sql.Append(
            CultureInfo.InvariantCulture,
            $"SELECT 'CTX_FLAG', hex(CAST(session_id AS BLOB)), turn_index FROM session_context_flagged_turn{childWhere} ORDER BY session_id ASC, turn_index ASC;\n");

        sql.Append(
            "SELECT 'CTX_TURN', hex(CAST(t.session_id AS BLOB)), t.position, t.input_tokens, t.bucketed_tokens, t.residual_tokens, " +
            "hex(CAST(t.residual_attribution AS BLOB)), t.builtin_tool_definition_tokens, t.fresh_input, " +
            "typeof(h.headroom), COALESCE(CAST(h.headroom AS TEXT), ''), " +
            "typeof(h.pressure), COALESCE(CAST(h.pressure AS TEXT), ''), " +
            "typeof(a.rate), COALESCE(CAST(a.rate AS TEXT), ''), " +
            "typeof(si.block_count), COALESCE(CAST(si.block_count AS TEXT), ''), " +
            "typeof(si.tokens), COALESCE(CAST(si.tokens AS TEXT), ''), " +
            "typeof(fj.previous_fresh_input), COALESCE(CAST(fj.previous_fresh_input AS TEXT), ''), " +
            "typeof(fj.factor), COALESCE(CAST(fj.factor AS TEXT), ''), " +
            "typeof(nr.tokens), COALESCE(CAST(nr.tokens AS TEXT), '') " +
            "FROM session_context_turn t " +
            "LEFT JOIN session_context_turn_headroom h ON h.session_id = t.session_id AND h.position = t.position " +
            "LEFT JOIN session_context_turn_accumulation a ON a.session_id = t.session_id AND a.position = t.position " +
            "LEFT JOIN session_context_turn_stripped_instructions si ON si.session_id = t.session_id AND si.position = t.position " +
            "LEFT JOIN session_context_turn_fresh_jump fj ON fj.session_id = t.session_id AND fj.position = t.position " +
            "LEFT JOIN session_context_turn_non_resident nr ON nr.session_id = t.session_id AND nr.position = t.position" +
            $"{childAliasedWhere} ORDER BY t.session_id ASC, t.position ASC;\n");

        sql.Append(
            CultureInfo.InvariantCulture,
            $"SELECT 'CTX_T_BKT', hex(CAST(session_id AS BLOB)), position, hex(CAST(bucket AS BLOB)), tokens FROM session_context_turn_bucket{childWhere} ORDER BY session_id ASC, position ASC, rowid ASC;\n");

        sql.Append(
            CultureInfo.InvariantCulture,
            $"SELECT 'CTX_T_SRV', hex(CAST(session_id AS BLOB)), position, hex(CAST(server AS BLOB)), tokens FROM session_context_turn_server_definitions{childWhere} ORDER BY session_id ASC, position ASC, rowid ASC;\n");

        sql.Append(
            "SELECT 'SCH', hex(CAST(t.session_id AS BLOB)), t.invocation_count, t.turn_count, " +
            "typeof(u.token_residencies), COALESCE(CAST(u.token_residencies AS TEXT), '') " +
            "FROM session_schema t " +
            $"LEFT JOIN session_schema_unused u ON u.session_id = t.session_id{childAliasedWhere};\n");

        sql.Append(
            "SELECT 'SCH_TOOL', hex(CAST(t.session_id AS BLOB)), t.position, hex(CAST(t.tool_name AS BLOB)), t.cost_tokens, t.invoked, " +
            "typeof(s.server_name), COALESCE(hex(CAST(s.server_name AS BLOB)), '') " +
            "FROM session_schema_ranked_tool t " +
            $"LEFT JOIN session_schema_ranked_tool_server s ON s.session_id = t.session_id AND s.position = t.position{childAliasedWhere} ORDER BY t.session_id ASC, t.position ASC;\n");

        sql.Append(
            CultureInfo.InvariantCulture,
            $"SELECT 'SCH_SRV', hex(CAST(session_id AS BLOB)), hex(CAST(server_name AS BLOB)), tokens FROM session_schema_server{childWhere} ORDER BY session_id ASC, rowid ASC;\n");

        sql.Append(
            CultureInfo.InvariantCulture,
            $"SELECT 'TL_SUB', hex(CAST(session_id AS BLOB)), hex(CAST(span_id AS BLOB)) FROM session_timeline_subagent{childWhere} ORDER BY session_id ASC, rowid ASC;\n");

        sql.Append(
            CultureInfo.InvariantCulture,
            $"SELECT 'TL_AUX', hex(CAST(session_id AS BLOB)), hex(CAST(span_id AS BLOB)) FROM session_timeline_auxiliary{childWhere} ORDER BY session_id ASC, rowid ASC;\n");

        string output = ExecuteSqlite(sql.ToString());
        List<StoredSessionHeader> projHeaders = [];
        Dictionary<string, List<string>> requestsBySess = new(StringComparer.Ordinal);
        Dictionary<string, List<StoredSessionTurn>> turnsBySess = new(StringComparer.Ordinal);
        Dictionary<string, RawContextHeader> ctxHeadersBySess = new(StringComparer.Ordinal);
        Dictionary<string, List<string>> obsBucketsBySess = new(StringComparer.Ordinal);
        Dictionary<string, List<StoredSessionContextDerivedCount>> derivedBySess = new(StringComparer.Ordinal);
        Dictionary<string, List<int>> flaggedBySess = new(StringComparer.Ordinal);
        Dictionary<string, List<RawContextTurnHeader>> ctxTurnsBySess = new(StringComparer.Ordinal);
        Dictionary<(string SessionId, int Position), List<StoredTurnBucketTokens>> ctxBucketsByTurn = [];
        Dictionary<(string SessionId, int Position), List<StoredTurnServerDefinitionTokens>> ctxServersByTurn = [];
        Dictionary<string, (int InvocationCount, int TurnCount, long? Unused)> schemaHeadersBySess = new(StringComparer.Ordinal);
        Dictionary<string, List<StoredSchemaRankedTool>> rankedToolsBySess = new(StringComparer.Ordinal);
        Dictionary<string, List<StoredSchemaServerTokens>> schemaServersBySess = new(StringComparer.Ordinal);
        Dictionary<string, List<string>> subagentsBySess = new(StringComparer.Ordinal);
        Dictionary<string, List<string>> auxiliariesBySess = new(StringComparer.Ordinal);

        foreach (string line in TabLines(output))
        {
            string[] cols = line.Split('\t');
            if (cols.Length < 2)
            {
                throw new StoredDataException("Unexpected short row in QuerySessionProjections.");
            }

            switch (cols[0])
            {
                case "PROJ":
                    if (cols.Length != 16)
                    {
                        throw new StoredDataException($"Expected 16 columns for PROJ row, got {cols.Length.ToString(CultureInfo.InvariantCulture)}.");
                    }

                    projHeaders.Add(ParseSessionHeader(cols, 1));
                    break;

                case "REQ":
                    if (cols.Length != 3)
                    {
                        throw new StoredDataException($"Expected 3 columns for REQ row, got {cols.Length.ToString(CultureInfo.InvariantCulture)}.");
                    }

                    GetOrCreateList(requestsBySess, ParseHexRequired(cols[1], "session_request.session_id"))
                        .Add(ParseHexRequired(cols[2], "session_request.request_span_id"));
                    break;

                case "TURN":
                    {
                        if (cols.Length != 4)
                        {
                            throw new StoredDataException($"Expected 4 columns for TURN row, got {cols.Length.ToString(CultureInfo.InvariantCulture)}.");
                        }

                        string sid = ParseHexRequired(cols[1], "session_turn.session_id");
                        int pos = ParseInt32Required(cols[2], "session_turn.position");
                        string spid = ParseHexRequired(cols[3], "session_turn.span_id");
                        GetOrCreateList(turnsBySess, sid).Add(new StoredSessionTurn(sid, pos, spid));
                        break;
                    }

                case "CTX":
                    {
                        if (cols.Length != 7)
                        {
                            throw new StoredDataException($"Expected 7 columns for CTX row, got {cols.Length.ToString(CultureInfo.InvariantCulture)}.");
                        }

                        string sid = ParseHexRequired(cols[1], "session_context.session_id");
                        ctxHeadersBySess[sid] = new RawContextHeader(
                            ParseInt64Required(cols[2], "session_context.context_limit"),
                            ParseHexRequired(cols[3], "session_context.context_limit_source"),
                            ParseInt64Required(cols[4], "session_context.residual_total"),
                            ParseDoubleRequired(cols[5], "session_context.fresh_jump_factor"),
                            ParseDoubleRequired(cols[6], "session_context.accumulation_rate"));
                        break;
                    }

                case "CTX_OBS":
                    if (cols.Length != 3)
                    {
                        throw new StoredDataException($"Expected 3 columns for CTX_OBS row, got {cols.Length.ToString(CultureInfo.InvariantCulture)}.");
                    }

                    GetOrCreateList(obsBucketsBySess, ParseHexRequired(cols[1], "session_context_observed_part.session_id"))
                        .Add(ParseHexRequired(cols[2], "session_context_observed_part.bucket"));
                    break;

                case "CTX_DER":
                    if (cols.Length != 4)
                    {
                        throw new StoredDataException($"Expected 4 columns for CTX_DER row, got {cols.Length.ToString(CultureInfo.InvariantCulture)}.");
                    }

                    GetOrCreateList(derivedBySess, ParseHexRequired(cols[1], "session_context_derived_count.session_id"))
                        .Add(new StoredSessionContextDerivedCount(
                            ParseHexRequired(cols[2], "session_context_derived_count.counter"),
                            ParseInt64Required(cols[3], "session_context_derived_count.count")));
                    break;

                case "CTX_FLAG":
                    if (cols.Length != 3)
                    {
                        throw new StoredDataException($"Expected 3 columns for CTX_FLAG row, got {cols.Length.ToString(CultureInfo.InvariantCulture)}.");
                    }

                    GetOrCreateList(flaggedBySess, ParseHexRequired(cols[1], "session_context_flagged_turn.session_id"))
                        .Add(ParseInt32Required(cols[2], "session_context_flagged_turn.turn_index"));
                    break;

                case "CTX_TURN":
                    if (cols.Length != 25)
                    {
                        throw new StoredDataException($"Expected 25 columns for CTX_TURN row, got {cols.Length.ToString(CultureInfo.InvariantCulture)}.");
                    }

                    GetOrCreateList(ctxTurnsBySess, ParseHexRequired(cols[1], "session_context_turn.session_id"))
                        .Add(new RawContextTurnHeader(
                            ParseInt32Required(cols[2], "session_context_turn.position"),
                            ParseInt64Required(cols[3], "session_context_turn.input_tokens"),
                            ParseInt64Required(cols[4], "session_context_turn.bucketed_tokens"),
                            ParseInt64Required(cols[5], "session_context_turn.residual_tokens"),
                            ParseHexRequired(cols[6], "session_context_turn.residual_attribution"),
                            ParseInt64Required(cols[7], "session_context_turn.builtin_tool_definition_tokens"),
                            ParseInt64Required(cols[8], "session_context_turn.fresh_input"),
                            ParseNullableInt64(cols[9], cols[10], "session_context_turn_headroom.headroom"),
                            ParseNullableDouble(cols[11], cols[12], "session_context_turn_headroom.pressure"),
                            ParseNullableDouble(cols[13], cols[14], "session_context_turn_accumulation.rate"),
                            ParseNullableInt64(cols[15], cols[16], "session_context_turn_stripped_instructions.block_count"),
                            ParseNullableInt64(cols[17], cols[18], "session_context_turn_stripped_instructions.tokens"),
                            ParseNullableInt64(cols[19], cols[20], "session_context_turn_fresh_jump.previous_fresh_input"),
                            ParseNullableDouble(cols[21], cols[22], "session_context_turn_fresh_jump.factor"),
                            ParseNullableInt64(cols[23], cols[24], "session_context_turn_non_resident.tokens")));
                    break;

                case "CTX_T_BKT":
                    {
                        if (cols.Length != 5)
                        {
                            throw new StoredDataException($"Expected 5 columns for CTX_T_BKT row, got {cols.Length.ToString(CultureInfo.InvariantCulture)}.");
                        }

                        (string, int) key = (ParseHexRequired(cols[1], "session_context_turn_bucket.session_id"), ParseInt32Required(cols[2], "session_context_turn_bucket.position"));
                        GetOrCreateList(ctxBucketsByTurn, key).Add(new StoredTurnBucketTokens(
                            ParseHexRequired(cols[3], "session_context_turn_bucket.bucket"),
                            ParseInt64Required(cols[4], "session_context_turn_bucket.tokens")));
                        break;
                    }

                case "CTX_T_SRV":
                    {
                        if (cols.Length != 5)
                        {
                            throw new StoredDataException($"Expected 5 columns for CTX_T_SRV row, got {cols.Length.ToString(CultureInfo.InvariantCulture)}.");
                        }

                        (string, int) key = (ParseHexRequired(cols[1], "session_context_turn_server_definitions.session_id"), ParseInt32Required(cols[2], "session_context_turn_server_definitions.position"));
                        GetOrCreateList(ctxServersByTurn, key).Add(new StoredTurnServerDefinitionTokens(
                            ParseHexRequired(cols[3], "session_context_turn_server_definitions.server"),
                            ParseInt64Required(cols[4], "session_context_turn_server_definitions.tokens")));
                        break;
                    }

                case "SCH":
                    if (cols.Length != 6)
                    {
                        throw new StoredDataException($"Expected 6 columns for SCH row, got {cols.Length.ToString(CultureInfo.InvariantCulture)}.");
                    }

                    schemaHeadersBySess[ParseHexRequired(cols[1], "session_schema.session_id")] = (
                        ParseInt32Required(cols[2], "session_schema.invocation_count"),
                        ParseInt32Required(cols[3], "session_schema.turn_count"),
                        ParseNullableInt64(cols[4], cols[5], "session_schema_unused.token_residencies"));
                    break;

                case "SCH_TOOL":
                    if (cols.Length != 8)
                    {
                        throw new StoredDataException($"Expected 8 columns for SCH_TOOL row, got {cols.Length.ToString(CultureInfo.InvariantCulture)}.");
                    }

                    GetOrCreateList(rankedToolsBySess, ParseHexRequired(cols[1], "session_schema_ranked_tool.session_id"))
                        .Add(new StoredSchemaRankedTool(
                            ParseInt32Required(cols[2], "session_schema_ranked_tool.position"),
                            ParseHexRequired(cols[3], "session_schema_ranked_tool.tool_name"),
                            ParseInt64Required(cols[4], "session_schema_ranked_tool.cost_tokens"),
                            ParseBoolFlag(cols[5], "session_schema_ranked_tool.invoked"),
                            ParseNullableHex(cols[6], cols[7], "session_schema_ranked_tool_server.server_name")));
                    break;

                case "SCH_SRV":
                    if (cols.Length != 4)
                    {
                        throw new StoredDataException($"Expected 4 columns for SCH_SRV row, got {cols.Length.ToString(CultureInfo.InvariantCulture)}.");
                    }

                    GetOrCreateList(schemaServersBySess, ParseHexRequired(cols[1], "session_schema_server.session_id"))
                        .Add(new StoredSchemaServerTokens(
                            ParseHexRequired(cols[2], "session_schema_server.server_name"),
                            ParseInt64Required(cols[3], "session_schema_server.tokens")));
                    break;

                case "TL_SUB":
                    if (cols.Length != 3)
                    {
                        throw new StoredDataException($"Expected 3 columns for TL_SUB row, got {cols.Length.ToString(CultureInfo.InvariantCulture)}.");
                    }

                    GetOrCreateList(subagentsBySess, ParseHexRequired(cols[1], "session_timeline_subagent.session_id"))
                        .Add(ParseHexRequired(cols[2], "session_timeline_subagent.span_id"));
                    break;

                case "TL_AUX":
                    if (cols.Length != 3)
                    {
                        throw new StoredDataException($"Expected 3 columns for TL_AUX row, got {cols.Length.ToString(CultureInfo.InvariantCulture)}.");
                    }

                    GetOrCreateList(auxiliariesBySess, ParseHexRequired(cols[1], "session_timeline_auxiliary.session_id"))
                        .Add(ParseHexRequired(cols[2], "session_timeline_auxiliary.span_id"));
                    break;

                default:
                    throw new StoredDataException($"Unexpected projection query row tag '{cols[0]}'.");
            }
        }

        List<StoredSessionProjection> projections = new(projHeaders.Count);
        foreach (StoredSessionHeader header in projHeaders)
        {
            StoredSessionContext? context = null;
            if (ctxHeadersBySess.TryGetValue(header.SessionId, out RawContextHeader? ctxHeader))
            {
                List<RawContextTurnHeader> rawTurns = ctxTurnsBySess.TryGetValue(header.SessionId, out List<RawContextTurnHeader>? ct)
                    ? ct
                    : [];
                List<StoredSessionContextTurn> ctxTurns = new(rawTurns.Count);
                foreach (RawContextTurnHeader rt in rawTurns)
                {
                    (string, int) turnKey = (header.SessionId, rt.Position);
                    ctxTurns.Add(new StoredSessionContextTurn(
                        rt.Position,
                        rt.InputTokens,
                        rt.BucketedTokens,
                        rt.ResidualTokens,
                        rt.ResidualAttribution,
                        rt.BuiltinToolDefinitionTokens,
                        rt.FreshInput,
                        ctxBucketsByTurn.TryGetValue(turnKey, out List<StoredTurnBucketTokens>? bkts) ? bkts : [],
                        ctxServersByTurn.TryGetValue(turnKey, out List<StoredTurnServerDefinitionTokens>? srvs) ? srvs : [],
                        rt.Headroom,
                        rt.Pressure,
                        rt.AccumulationRate,
                        rt.StrippedBlockCount,
                        rt.StrippedTokens,
                        rt.PreviousFreshInput,
                        rt.FreshJumpFactor,
                        rt.NonResidentTokens));
                }

                context = new StoredSessionContext(
                    ctxHeader.ContextLimit,
                    ctxHeader.ContextLimitSource,
                    ctxHeader.ResidualTotal,
                    ctxHeader.FreshJumpFactor,
                    ctxHeader.AccumulationRate,
                    obsBucketsBySess.TryGetValue(header.SessionId, out List<string>? obs) ? obs : [],
                    derivedBySess.TryGetValue(header.SessionId, out List<StoredSessionContextDerivedCount>? der) ? der : [],
                    flaggedBySess.TryGetValue(header.SessionId, out List<int>? flg) ? flg : [],
                    ctxTurns);
            }

            StoredSessionSchema? schema = null;
            if (schemaHeadersBySess.TryGetValue(header.SessionId, out (int InvocationCount, int TurnCount, long? Unused) schHeader))
            {
                schema = new StoredSessionSchema(
                    schHeader.InvocationCount,
                    schHeader.TurnCount,
                    rankedToolsBySess.TryGetValue(header.SessionId, out List<StoredSchemaRankedTool>? rtList) ? rtList : [],
                    schemaServersBySess.TryGetValue(header.SessionId, out List<StoredSchemaServerTokens>? ssList) ? ssList : [],
                    schHeader.Unused);
            }

            IReadOnlyList<string>? timelineSubagents = subagentsBySess.TryGetValue(header.SessionId, out List<string>? subList) && subList.Count > 0
                ? subList
                : null;
            IReadOnlyList<string>? timelineAuxiliaries = auxiliariesBySess.TryGetValue(header.SessionId, out List<string>? auxList) && auxList.Count > 0
                ? auxList
                : null;

            projections.Add(new StoredSessionProjection(
                header.SessionId,
                header.Label,
                header.IsSubagent,
                header.SpanCount,
                header.StartedAtUtc,
                header.EndedAtUtc,
                header.ParentSessionId,
                header.AgentName,
                header.IncludedInParent,
                requestsBySess.TryGetValue(header.SessionId, out List<string>? reqList) ? reqList : [],
                turnsBySess.TryGetValue(header.SessionId, out List<StoredSessionTurn>? turnList) ? turnList : [],
                context,
                header.ContextUnmeasurableReason,
                schema,
                header.SchemaUnmeasurableReason,
                timelineSubagents,
                timelineAuxiliaries));
        }

        return projections;
    }

    private IReadOnlyList<StoredRun> QueryRuns(string? filterPredicate)
    {
        string whereClause = filterPredicate is null ? string.Empty : $" WHERE {filterPredicate}";
        string sql = TabReadScriptHeader +
                     "SELECT hex(CAST(r.run_id AS BLOB)), hex(CAST(r.harness_id AS BLOB)), hex(CAST(r.grouping_basis AS BLOB)), hex(CAST(r.grouping_rule AS BLOB)), " +
                     "r.execution_count, " +
                     "typeof(rl.label), COALESCE(hex(CAST(rl.label AS BLOB)), ''), " +
                     "typeof(rw.working_directory), COALESCE(hex(CAST(rw.working_directory AS BLOB)), ''), " +
                     "typeof(rb.started_at_utc), COALESCE(hex(CAST(rb.started_at_utc AS BLOB)), ''), " +
                     "typeof(rb.ended_at_utc), COALESCE(hex(CAST(rb.ended_at_utc AS BLOB)), '') " +
                     "FROM run r " +
                     "LEFT JOIN run_label rl ON rl.run_id = r.run_id " +
                     "LEFT JOIN run_working_directory rw ON rw.run_id = r.run_id " +
                     $"LEFT JOIN run_bounds rb ON rb.run_id = r.run_id{whereClause} ORDER BY r.rowid ASC;\n";

        string output = ExecuteSqlite(sql);
        List<StoredRun> results = [];
        foreach (string line in TabLines(output))
        {
            string[] cols = SplitTabLine(line, 13, "run");
            results.Add(new StoredRun(
                ParseHexRequired(cols[0], "run.run_id"),
                ParseHexRequired(cols[1], "run.harness_id"),
                ParseHexRequired(cols[2], "run.grouping_basis"),
                ParseHexRequired(cols[3], "run.grouping_rule"),
                ParseInt32Required(cols[4], "run.execution_count"),
                ParseNullableHex(cols[5], cols[6], "run_label.label"),
                ParseNullableHex(cols[7], cols[8], "run_working_directory.working_directory"),
                ParseNullableHex(cols[9], cols[10], "run_bounds.started_at_utc"),
                ParseNullableHex(cols[11], cols[12], "run_bounds.ended_at_utc")));
        }

        return results;
    }

    private IReadOnlyList<StoredExecution> QueryExecutions(string? filterPredicate)
    {
        string whereClause = filterPredicate is null ? string.Empty : $" WHERE {filterPredicate}";
        string sql = TabReadScriptHeader +
                     "SELECT hex(CAST(e.execution_id AS BLOB)), hex(CAST(e.run_id AS BLOB)), hex(CAST(e.harness_id AS BLOB)), e.is_root, " +
                     "typeof(es.session_id), COALESCE(hex(CAST(es.session_id AS BLOB)), ''), " +
                     "typeof(ep.parent_execution_id), COALESCE(hex(CAST(ep.parent_execution_id AS BLOB)), ''), " +
                     "typeof(ea.agent_name), COALESCE(hex(CAST(ea.agent_name AS BLOB)), ''), " +
                     "typeof(eb.started_at_utc), COALESCE(hex(CAST(eb.started_at_utc AS BLOB)), ''), " +
                     "typeof(eb.ended_at_utc), COALESCE(hex(CAST(eb.ended_at_utc AS BLOB)), '') " +
                     "FROM execution e " +
                     "LEFT JOIN execution_session es ON es.execution_id = e.execution_id " +
                     "LEFT JOIN execution_parent ep ON ep.execution_id = e.execution_id " +
                     "LEFT JOIN execution_agent ea ON ea.execution_id = e.execution_id " +
                     $"LEFT JOIN execution_bounds eb ON eb.execution_id = e.execution_id{whereClause} ORDER BY e.rowid ASC;\n";

        string output = ExecuteSqlite(sql);
        List<StoredExecution> results = [];
        foreach (string line in TabLines(output))
        {
            string[] cols = SplitTabLine(line, 14, "execution");
            results.Add(new StoredExecution(
                ParseHexRequired(cols[0], "execution.execution_id"),
                ParseHexRequired(cols[1], "execution.run_id"),
                ParseHexRequired(cols[2], "execution.harness_id"),
                ParseBoolFlag(cols[3], "execution.is_root"),
                ParseNullableHex(cols[4], cols[5], "execution_session.session_id"),
                ParseNullableHex(cols[6], cols[7], "execution_parent.parent_execution_id"),
                ParseNullableHex(cols[8], cols[9], "execution_agent.agent_name"),
                ParseNullableHex(cols[10], cols[11], "execution_bounds.started_at_utc"),
                ParseNullableHex(cols[12], cols[13], "execution_bounds.ended_at_utc")));
        }

        return results;
    }

    private IReadOnlyList<StoredHarnessRollup> QueryHarnessRollups(string? filterPredicate)
    {
        string whereClause = filterPredicate is null ? string.Empty : $" WHERE {filterPredicate}";
        string sql = TabReadScriptHeader +
                     "SELECT 'ROLLUP', hex(CAST(harness_id AS BLOB)), session_count, run_count, execution_count, unknown_window_sessions, window_sessions_total " +
                     $"FROM harness_rollup{whereClause} ORDER BY harness_id ASC;\n" +
                     "SELECT 'METRIC', hex(CAST(harness_id AS BLOB)), hex(CAST(metric AS BLOB)), value, hex(CAST(basis AS BLOB)) " +
                     $"FROM harness_rollup_metric{whereClause} ORDER BY harness_id ASC, rowid ASC;\n";

        string output = ExecuteSqlite(sql);
        List<(string HarnessId, int Sessions, int Runs, int Executions, int Unknown, int Total)> headers = [];
        Dictionary<string, List<StoredHarnessRollupMetric>> metricsByHarness = new(StringComparer.Ordinal);

        foreach (string line in TabLines(output))
        {
            string[] cols = line.Split('\t');
            if (cols.Length < 1)
            {
                throw new StoredDataException("Unexpected empty row in QueryHarnessRollups.");
            }

            switch (cols[0])
            {
                case "ROLLUP":
                    if (cols.Length != 7)
                    {
                        throw new StoredDataException($"Expected 7 columns for ROLLUP row, got {cols.Length.ToString(CultureInfo.InvariantCulture)}.");
                    }

                    headers.Add((
                        ParseHexRequired(cols[1], "harness_rollup.harness_id"),
                        ParseInt32Required(cols[2], "harness_rollup.session_count"),
                        ParseInt32Required(cols[3], "harness_rollup.run_count"),
                        ParseInt32Required(cols[4], "harness_rollup.execution_count"),
                        ParseInt32Required(cols[5], "harness_rollup.unknown_window_sessions"),
                        ParseInt32Required(cols[6], "harness_rollup.window_sessions_total")));
                    break;

                case "METRIC":
                    if (cols.Length != 5)
                    {
                        throw new StoredDataException($"Expected 5 columns for METRIC row, got {cols.Length.ToString(CultureInfo.InvariantCulture)}.");
                    }

                    GetOrCreateList(metricsByHarness, ParseHexRequired(cols[1], "harness_rollup_metric.harness_id"))
                        .Add(new StoredHarnessRollupMetric(
                            ParseHexRequired(cols[2], "harness_rollup_metric.metric"),
                            ParseDoubleRequired(cols[3], "harness_rollup_metric.value"),
                            ParseHexRequired(cols[4], "harness_rollup_metric.basis")));
                    break;

                default:
                    throw new StoredDataException($"Unexpected harness rollup query row tag '{cols[0]}'.");
            }
        }

        List<StoredHarnessRollup> rollups = new(headers.Count);
        foreach ((string harnessId, int sess, int runs, int execs, int unk, int tot) in headers)
        {
            rollups.Add(new StoredHarnessRollup(
                harnessId,
                sess,
                runs,
                execs,
                unk,
                tot,
                metricsByHarness.TryGetValue(harnessId, out List<StoredHarnessRollupMetric>? metrics) ? metrics : []));
        }

        return rollups;
    }

    private IReadOnlyList<StoredFinding> QueryFindings(string? filterPredicate)
    {
        string whereClause = filterPredicate is null ? string.Empty : $" WHERE {filterPredicate}";
        string linkFilter = filterPredicate is null
            ? string.Empty
            : " WHERE fel.finding_id IN (SELECT f.finding_id FROM finding f LEFT JOIN finding_run fr ON fr.finding_id = f.finding_id LEFT JOIN finding_session fs ON fs.finding_id = f.finding_id WHERE " + filterPredicate + ")";

        string sql = TabReadScriptHeader +
                     "SELECT 'FIND', hex(CAST(f.finding_id AS BLOB)), hex(CAST(f.detector_id AS BLOB)), hex(CAST(f.title AS BLOB)), hex(CAST(f.mechanism AS BLOB)), " +
                     "hex(CAST(f.confidence AS BLOB)), hex(CAST(f.recommendation AS BLOB)), hex(CAST(f.outcome_risk_caveat AS BLOB)), f.rank_score, " +
                     "typeof(few.tokens), COALESCE(CAST(few.tokens AS TEXT), ''), " +
                     "typeof(feb.lower), COALESCE(CAST(feb.lower AS TEXT), ''), " +
                     "typeof(feb.upper), COALESCE(CAST(feb.upper AS TEXT), ''), " +
                     "typeof(fr.run_id), COALESCE(hex(CAST(fr.run_id AS BLOB)), ''), " +
                     "typeof(fs.session_id), COALESCE(hex(CAST(fs.session_id AS BLOB)), ''), " +
                     "typeof(fmc.measurement_class), COALESCE(hex(CAST(fmc.measurement_class AS BLOB)), ''), " +
                     "typeof(fcb.confidence_basis), COALESCE(hex(CAST(fcb.confidence_basis AS BLOB)), ''), " +
                     "typeof(fww.text), COALESCE(hex(CAST(fww.text AS BLOB)), ''), " +
                     "typeof(fch.context_limit), COALESCE(CAST(fch.context_limit AS TEXT), ''), " +
                     "typeof(fch.context_limit_source), COALESCE(hex(CAST(fch.context_limit_source AS BLOB)), '') " +
                     "FROM finding f " +
                     "LEFT JOIN finding_estimated_waste few ON few.finding_id = f.finding_id " +
                     "LEFT JOIN finding_error_bar feb ON feb.finding_id = f.finding_id " +
                     "LEFT JOIN finding_run fr ON fr.finding_id = f.finding_id " +
                     "LEFT JOIN finding_session fs ON fs.finding_id = f.finding_id " +
                     "LEFT JOIN finding_measurement_class fmc ON fmc.finding_id = f.finding_id " +
                     "LEFT JOIN finding_confidence_basis fcb ON fcb.finding_id = f.finding_id " +
                     "LEFT JOIN finding_what_would_raise_it fww ON fww.finding_id = f.finding_id " +
                     $"LEFT JOIN finding_compaction_hazard fch ON fch.finding_id = f.finding_id{whereClause} " +
                     "ORDER BY f.rank_score DESC, f.rowid ASC;\n" +
                     "SELECT 'LINK', hex(CAST(fel.finding_id AS BLOB)), hex(CAST(fel.span_id AS BLOB)), fel.turn_index, hex(CAST(fel.description AS BLOB)) " +
                     $"FROM finding_evidence_link fel{linkFilter} ORDER BY fel.finding_id ASC, fel.position ASC;\n";

        string output = ExecuteSqlite(sql);
        List<RawFindingHeader> headers = [];
        Dictionary<string, List<FindingEvidenceLink>> linksByFinding = new(StringComparer.Ordinal);

        foreach (string line in TabLines(output))
        {
            string[] cols = line.Split('\t');
            if (cols.Length < 1)
            {
                throw new StoredDataException("Unexpected empty row in QueryFindings.");
            }

            switch (cols[0])
            {
                case "FIND":
                    {
                        if (cols.Length != 29)
                        {
                            throw new StoredDataException($"Expected 29 columns for FIND row, got {cols.Length.ToString(CultureInfo.InvariantCulture)}.");
                        }

                        double? lower = ParseNullableDouble(cols[11], cols[12], "finding_error_bar.lower");
                        double? upper = ParseNullableDouble(cols[13], cols[14], "finding_error_bar.upper");
                        FindingErrorBar? errorBar = lower.HasValue && upper.HasValue
                            ? new FindingErrorBar((long)lower.Value, (long)upper.Value)
                            : lower.HasValue || upper.HasValue
                                ? throw new StoredDataException("Partial finding_error_bar row returned.")
                                : null;

                        long? hazardLimit = ParseNullableInt64(cols[25], cols[26], "finding_compaction_hazard.context_limit");
                        string? hazardSource = ParseNullableHex(cols[27], cols[28], "finding_compaction_hazard.context_limit_source");
                        StoredCompactionHazard? hazard = hazardLimit.HasValue && hazardSource is not null
                            ? new StoredCompactionHazard(hazardLimit.Value, hazardSource)
                            : hazardLimit.HasValue || hazardSource is not null
                                ? throw new StoredDataException("Partial finding_compaction_hazard row returned.")
                                : null;

                        headers.Add(new RawFindingHeader(
                            ParseHexRequired(cols[1], "finding.finding_id"),
                            ParseHexRequired(cols[2], "finding.detector_id"),
                            ParseHexRequired(cols[3], "finding.title"),
                            ParseHexRequired(cols[4], "finding.mechanism"),
                            ParseHexRequired(cols[5], "finding.confidence"),
                            ParseHexRequired(cols[6], "finding.recommendation"),
                            ParseHexRequired(cols[7], "finding.outcome_risk_caveat"),
                            ParseDoubleRequired(cols[8], "finding.rank_score"),
                            ParseNullableInt64(cols[9], cols[10], "finding_estimated_waste.tokens"),
                            errorBar,
                            ParseNullableHex(cols[15], cols[16], "finding_run.run_id"),
                            ParseNullableHex(cols[17], cols[18], "finding_session.session_id"),
                            ParseNullableHex(cols[19], cols[20], "finding_measurement_class.measurement_class"),
                            ParseNullableHex(cols[21], cols[22], "finding_confidence_basis.confidence_basis"),
                            ParseNullableHex(cols[23], cols[24], "finding_what_would_raise_it.text"),
                            hazard));
                        break;
                    }

                case "LINK":
                    if (cols.Length != 5)
                    {
                        throw new StoredDataException($"Expected 5 columns for LINK row, got {cols.Length.ToString(CultureInfo.InvariantCulture)}.");
                    }

                    GetOrCreateList(linksByFinding, ParseHexRequired(cols[1], "finding_evidence_link.finding_id"))
                        .Add(new FindingEvidenceLink(
                            ParseHexRequired(cols[2], "finding_evidence_link.span_id"),
                            ParseInt32Required(cols[3], "finding_evidence_link.turn_index"),
                            ParseHexRequired(cols[4], "finding_evidence_link.description")));
                    break;

                default:
                    throw new StoredDataException($"Unexpected finding query row tag '{cols[0]}'.");
            }
        }

        List<StoredFinding> findings = new(headers.Count);
        foreach (RawFindingHeader h in headers)
        {
            findings.Add(new StoredFinding(
                h.FindingId,
                h.DetectorId,
                h.Title,
                h.Mechanism,
                h.Confidence,
                h.Recommendation,
                h.OutcomeRiskCaveat,
                h.RankScore,
                linksByFinding.TryGetValue(h.FindingId, out List<FindingEvidenceLink>? links) ? links : [],
                h.EstimatedWasteTokens,
                h.ErrorBar,
                h.RunId,
                h.SessionId,
                h.MeasurementClass,
                h.ConfidenceBasis,
                h.WhatWouldRaiseIt,
                h.CompactionHazard));
        }

        return findings;
    }

    private IReadOnlyList<StoredPrediction> QueryPredictions(string? filterPredicate)
    {
        string whereClause = filterPredicate is null ? string.Empty : $" WHERE {filterPredicate}";
        string sql = TabReadScriptHeader +
                     "SELECT hex(CAST(p.prediction_id AS BLOB)), hex(CAST(p.finding_id AS BLOB)), hex(CAST(p.run_id AS BLOB)), " +
                     "p.predicted_waste_tokens, p.confidence, hex(CAST(p.confidence_tier AS BLOB)), hex(CAST(p.created_at_utc AS BLOB)), " +
                     "typeof(pc.comparison_run_id), COALESCE(hex(CAST(pc.comparison_run_id AS BLOB)), ''), " +
                     "typeof(pod.tokens), COALESCE(CAST(pod.tokens AS TEXT), ''), " +
                     "typeof(pcal.score), COALESCE(CAST(pcal.score AS TEXT), '') " +
                     "FROM prediction p " +
                     "LEFT JOIN prediction_comparison pc ON pc.prediction_id = p.prediction_id " +
                     "LEFT JOIN prediction_observed_delta pod ON pod.prediction_id = p.prediction_id " +
                     $"LEFT JOIN prediction_calibration pcal ON pcal.prediction_id = p.prediction_id{whereClause} " +
                     "ORDER BY p.rowid ASC;\n";

        string output = ExecuteSqlite(sql);
        List<StoredPrediction> results = [];
        foreach (string line in TabLines(output))
        {
            string[] cols = SplitTabLine(line, 13, "prediction");
            results.Add(new StoredPrediction(
                ParseHexRequired(cols[0], "prediction.prediction_id"),
                ParseHexRequired(cols[1], "prediction.finding_id"),
                ParseHexRequired(cols[2], "prediction.run_id"),
                ParseInt64Required(cols[3], "prediction.predicted_waste_tokens"),
                ParseDoubleRequired(cols[4], "prediction.confidence"),
                ParseHexRequired(cols[5], "prediction.confidence_tier"),
                ParseHexRequired(cols[6], "prediction.created_at_utc"),
                ParseNullableHex(cols[7], cols[8], "prediction_comparison.comparison_run_id"),
                ParseNullableInt64(cols[9], cols[10], "prediction_observed_delta.tokens"),
                ParseNullableDouble(cols[11], cols[12], "prediction_calibration.score")));
        }

        return results;
    }

    private static string BuildSpanFilterWhere(string alias, string? spanId, string? sessionKey)
    {
        List<string> predicates = [];
        if (spanId is not null)
        {
            predicates.Add($"{alias}.span_id = {TextExpr(spanId)}");
        }

        if (sessionKey is not null)
        {
            predicates.Add($"EXISTS (SELECT 1 FROM record_session_link rsl WHERE rsl.span_id = {alias}.span_id AND rsl.session_key = {TextExpr(sessionKey)})");
        }

        return predicates.Count > 0 ? " WHERE " + string.Join(" AND ", predicates) : string.Empty;
    }

    private static string BuildContentPartSelect(string alias, string whereClause, string orderBy) =>
        $"SELECT hex(CAST({alias}.span_id AS BLOB)), {BuildContentPartColumns(alias)} " +
        BuildContentPartFrom(alias) +
        $"{whereClause} {orderBy};\n";

    /// <summary>
    /// Builds the budgeted content-part read: the scoped rows are wrapped in a subquery whose
    /// window totals the UTF-8 bytes of every part before each row, so SQLite returns only the
    /// rows whose preceding parts stay under <paramref name="budget"/> plus the one row that
    /// crosses it — the same prefix an in-memory cut kept — and the text past the budget never
    /// leaves the database.
    /// </summary>
    /// <param name="alias">Content-part table alias.</param>
    /// <param name="whereClause">Scoped predicate clause, starting with WHERE.</param>
    /// <param name="budget">Maximum UTF-8 bytes of part text to keep.</param>
    private static string BuildBudgetedContentPartSelect(string alias, string whereClause, int budget) =>
        "SELECT * FROM (" +
        $"SELECT hex(CAST({alias}.span_id AS BLOB)) AS span_id, {BuildContentPartColumns(alias)}, " +
        "COALESCE(SUM(COALESCE(length(CAST(cpt.part_text AS BLOB)), 0)) OVER (" +
        $"ORDER BY {alias}.span_id ASC, {alias}.position ASC " +
        "ROWS BETWEEN UNBOUNDED PRECEDING AND 1 PRECEDING), 0) AS bytes_before " +
        BuildContentPartFrom(alias) +
        $"{whereClause}) WHERE bytes_before < {budget.ToString(CultureInfo.InvariantCulture)} " +
        "ORDER BY span_id ASC, position ASC;\n";

    private static string BuildContentPartFrom(string alias) =>
        $"FROM content_part {alias} " +
        $"LEFT JOIN content_part_text cpt ON cpt.span_id = {alias}.span_id AND cpt.position = {alias}.position " +
        $"LEFT JOIN content_part_withheld cpw ON cpw.span_id = {alias}.span_id AND cpw.position = {alias}.position " +
        $"LEFT JOIN content_part_reported_tokens cprt ON cprt.span_id = {alias}.span_id AND cprt.position = {alias}.position " +
        $"LEFT JOIN content_part_server cps ON cps.span_id = {alias}.span_id AND cps.position = {alias}.position " +
        $"LEFT JOIN content_part_role cpr ON cpr.span_id = {alias}.span_id AND cpr.position = {alias}.position " +
        $"LEFT JOIN content_part_residency cpres ON cpres.span_id = {alias}.span_id AND cpres.position = {alias}.position " +
        $"LEFT JOIN content_part_truncation cptr ON cptr.span_id = {alias}.span_id AND cptr.position = {alias}.position";

    private static string BuildContentPartColumns(string alias) =>
        $"{alias}.position, hex(CAST({alias}.bucket AS BLOB)), " +
        "typeof(cpt.part_text), COALESCE(hex(CAST(cpt.part_text AS BLOB)), ''), " +
        "typeof(cpw.utf8_bytes), COALESCE(CAST(cpw.utf8_bytes AS TEXT), ''), " +
        "typeof(cpw.characters), COALESCE(CAST(cpw.characters AS TEXT), ''), " +
        "typeof(cpw.measured_tokens), COALESCE(CAST(cpw.measured_tokens AS TEXT), ''), " +
        "typeof(cprt.token_count), COALESCE(CAST(cprt.token_count AS TEXT), ''), " +
        "typeof(cps.server_name), COALESCE(hex(CAST(cps.server_name AS BLOB)), ''), " +
        "typeof(cpr.role_name), COALESCE(hex(CAST(cpr.role_name AS BLOB)), ''), " +
        "typeof(cpres.is_resident), COALESCE(CAST(cpres.is_resident AS TEXT), ''), " +
        "typeof(cptr.original_utf8_bytes), COALESCE(CAST(cptr.original_utf8_bytes AS TEXT), '')";

    private static CollectedContentPart ParseContentPartColumns(string[] cols, int startIndex)
    {
        int position = ParseInt32Required(cols[startIndex], "content_part.position");
        ContentBucket bucket = ParseContentBucket(ParseHexRequired(cols[startIndex + 1], "content_part.bucket"));
        string? plainText = ParseNullableHex(cols[startIndex + 2], cols[startIndex + 3], "content_part_text.part_text");
        long? wBytes = ParseNullableInt64(cols[startIndex + 4], cols[startIndex + 5], "content_part_withheld.utf8_bytes");
        long? wChars = ParseNullableInt64(cols[startIndex + 6], cols[startIndex + 7], "content_part_withheld.characters");
        long? wTokens = ParseNullableInt64(cols[startIndex + 8], cols[startIndex + 9], "content_part_withheld.measured_tokens");
        CollectedWithheldText? withheld = wBytes.HasValue && wChars.HasValue && wTokens.HasValue
            ? new CollectedWithheldText(wBytes.Value, wChars.Value, wTokens.Value)
            : wBytes.HasValue || wChars.HasValue || wTokens.HasValue
                ? throw new StoredDataException("Partial content_part_withheld row returned.")
                : null;
        long? reportedTokens = ParseNullableInt64(cols[startIndex + 10], cols[startIndex + 11], "content_part_reported_tokens.token_count");
        string? server = ParseNullableHex(cols[startIndex + 12], cols[startIndex + 13], "content_part_server.server_name");
        string? role = ParseNullableHex(cols[startIndex + 14], cols[startIndex + 15], "content_part_role.role_name");
        long? residentRaw = ParseNullableInt64(cols[startIndex + 16], cols[startIndex + 17], "content_part_residency.is_resident");
        bool? resident = residentRaw.HasValue
            ? residentRaw.Value switch
            {
                0L => false,
                1L => true,
                _ => throw new StoredDataException($"Invalid boolean flag '{residentRaw.Value.ToString(CultureInfo.InvariantCulture)}' in content_part_residency.is_resident."),
            }
            : null;
        long? originalBytes = ParseNullableInt64(cols[startIndex + 18], cols[startIndex + 19], "content_part_truncation.original_utf8_bytes");

        return new CollectedContentPart(position, bucket, plainText, withheld, reportedTokens, server, role, resident, originalBytes);
    }

    private static string BuildToolCallSelect(string alias, string whereClause, string orderBy) =>
        $"SELECT hex(CAST({alias}.span_id AS BLOB)), {BuildToolCallColumns(alias)} " +
        $"FROM tool_call {alias} " +
        $"LEFT JOIN tool_call_mcp tcm ON tcm.span_id = {alias}.span_id " +
        $"LEFT JOIN tool_call_input_size tcis ON tcis.span_id = {alias}.span_id " +
        $"LEFT JOIN tool_call_input_text tcit ON tcit.span_id = {alias}.span_id " +
        $"LEFT JOIN tool_call_input_fingerprint tcif ON tcif.span_id = {alias}.span_id " +
        $"LEFT JOIN tool_call_input_truncation tcitrc ON tcitrc.span_id = {alias}.span_id " +
        $"LEFT JOIN tool_call_result_size tcrs ON tcrs.span_id = {alias}.span_id " +
        $"LEFT JOIN tool_call_result_text tcrt ON tcrt.span_id = {alias}.span_id " +
        $"LEFT JOIN tool_call_result_truncation tcrtrc ON tcrtrc.span_id = {alias}.span_id " +
        $"LEFT JOIN tool_call_outcome tco ON tco.span_id = {alias}.span_id " +
        $"LEFT JOIN tool_call_error tce ON tce.span_id = {alias}.span_id " +
        $"LEFT JOIN tool_call_error_code tcec ON tcec.span_id = {alias}.span_id " +
        $"LEFT JOIN tool_call_exit_code tcex ON tcex.span_id = {alias}.span_id" +
        $"{whereClause} {orderBy};\n";

    private static string BuildToolCallColumns(string alias) =>
        $"hex(CAST({alias}.call_id AS BLOB)), hex(CAST({alias}.tool_name AS BLOB)), " +
        "typeof(tcm.server_name), COALESCE(hex(CAST(tcm.server_name AS BLOB)), ''), " +
        "typeof(tcm.server_tool_name), COALESCE(hex(CAST(tcm.server_tool_name AS BLOB)), ''), " +
        "typeof(tcis.utf8_bytes), COALESCE(CAST(tcis.utf8_bytes AS TEXT), ''), " +
        "typeof(tcis.characters), COALESCE(CAST(tcis.characters AS TEXT), ''), " +
        "typeof(tcit.text), COALESCE(hex(CAST(tcit.text AS BLOB)), ''), " +
        "typeof(tcif.sha256), COALESCE(hex(CAST(tcif.sha256 AS BLOB)), ''), " +
        "typeof(tcitrc.original_utf8_bytes), COALESCE(CAST(tcitrc.original_utf8_bytes AS TEXT), ''), " +
        "typeof(tcrs.utf8_bytes), COALESCE(CAST(tcrs.utf8_bytes AS TEXT), ''), " +
        "typeof(tcrs.characters), COALESCE(CAST(tcrs.characters AS TEXT), ''), " +
        "typeof(tcrt.text), COALESCE(hex(CAST(tcrt.text AS BLOB)), ''), " +
        "typeof(tcrtrc.original_utf8_bytes), COALESCE(CAST(tcrtrc.original_utf8_bytes AS TEXT), ''), " +
        "typeof(tco.succeeded), COALESCE(CAST(tco.succeeded AS TEXT), ''), " +
        "typeof(tce.message), COALESCE(hex(CAST(tce.message AS BLOB)), ''), " +
        "typeof(tcec.code), COALESCE(hex(CAST(tcec.code AS BLOB)), ''), " +
        "typeof(tcex.exit_code), COALESCE(CAST(tcex.exit_code AS TEXT), '')";

    /// <summary>
    /// Tool-call columns without the two content-bearing text expressions, in the same order as
    /// <see cref="BuildToolCallColumns"/> so both parses share one reader.
    /// </summary>
    private static string BuildToolCallMetadataColumns(string alias) =>
        $"hex(CAST({alias}.call_id AS BLOB)), hex(CAST({alias}.tool_name AS BLOB)), " +
        "typeof(tcm.server_name), COALESCE(hex(CAST(tcm.server_name AS BLOB)), ''), " +
        "typeof(tcm.server_tool_name), COALESCE(hex(CAST(tcm.server_tool_name AS BLOB)), ''), " +
        "typeof(tcis.utf8_bytes), COALESCE(CAST(tcis.utf8_bytes AS TEXT), ''), " +
        "typeof(tcis.characters), COALESCE(CAST(tcis.characters AS TEXT), ''), " +
        "'null', '', " +
        "typeof(tcif.sha256), COALESCE(hex(CAST(tcif.sha256 AS BLOB)), ''), " +
        "typeof(tcitrc.original_utf8_bytes), COALESCE(CAST(tcitrc.original_utf8_bytes AS TEXT), ''), " +
        "typeof(tcrs.utf8_bytes), COALESCE(CAST(tcrs.utf8_bytes AS TEXT), ''), " +
        "typeof(tcrs.characters), COALESCE(CAST(tcrs.characters AS TEXT), ''), " +
        "'null', '', " +
        "typeof(tcrtrc.original_utf8_bytes), COALESCE(CAST(tcrtrc.original_utf8_bytes AS TEXT), ''), " +
        "typeof(tco.succeeded), COALESCE(CAST(tco.succeeded AS TEXT), ''), " +
        "typeof(tce.message), COALESCE(hex(CAST(tce.message AS BLOB)), ''), " +
        "typeof(tcec.code), COALESCE(hex(CAST(tcec.code AS BLOB)), ''), " +
        "typeof(tcex.exit_code), COALESCE(CAST(tcex.exit_code AS TEXT), '')";

    private static CollectedToolCall ParseToolCallColumns(string[] cols, int startIndex)
    {
        // Count: 2 + (16 * 2)? Let's verify exact count:
        // +0: call_id
        // +1: tool_name
        // +2,+3: tcm.server_name
        // +4,+5: tcm.server_tool_name
        // +6,+7: tcis.utf8_bytes
        // +8,+9: tcis.characters
        // +10,+11: tcit.text
        // +12,+13: tcif.sha256
        // +14,+15: tcitrc.original_utf8_bytes
        // +16,+17: tcrs.utf8_bytes
        // +18,+19: tcrs.characters
        // +20,+21: tcrt.text
        // +22,+23: tcrtrc.original_utf8_bytes
        // +24,+25: tco.succeeded
        // +26,+27: tce.message
        // +28,+29: tcec.code
        // +30,+31: tcex.exit_code
        // Total = 32 columns!
        string callId = ParseHexRequired(cols[startIndex], "tool_call.call_id");
        string toolName = ParseHexRequired(cols[startIndex + 1], "tool_call.tool_name");
        string? mcpServer = ParseNullableHex(cols[startIndex + 2], cols[startIndex + 3], "tool_call_mcp.server_name");
        string? mcpTool = ParseNullableHex(cols[startIndex + 4], cols[startIndex + 5], "tool_call_mcp.server_tool_name");

        long? inBytes = ParseNullableInt64(cols[startIndex + 6], cols[startIndex + 7], "tool_call_input_size.utf8_bytes");
        long? inChars = ParseNullableInt64(cols[startIndex + 8], cols[startIndex + 9], "tool_call_input_size.characters");
        string? inText = ParseNullableHex(cols[startIndex + 10], cols[startIndex + 11], "tool_call_input_text.text");
        string? inSha = ParseNullableHex(cols[startIndex + 12], cols[startIndex + 13], "tool_call_input_fingerprint.sha256");
        long? inTrunc = ParseNullableInt64(cols[startIndex + 14], cols[startIndex + 15], "tool_call_input_truncation.original_utf8_bytes");
        CollectedToolContent? input = inBytes.HasValue && inChars.HasValue
            ? new CollectedToolContent(inText, inBytes.Value, inChars.Value, inSha, inTrunc)
            : null;

        long? resBytes = ParseNullableInt64(cols[startIndex + 16], cols[startIndex + 17], "tool_call_result_size.utf8_bytes");
        long? resChars = ParseNullableInt64(cols[startIndex + 18], cols[startIndex + 19], "tool_call_result_size.characters");
        string? resText = ParseNullableHex(cols[startIndex + 20], cols[startIndex + 21], "tool_call_result_text.text");
        long? resTrunc = ParseNullableInt64(cols[startIndex + 22], cols[startIndex + 23], "tool_call_result_truncation.original_utf8_bytes");
        CollectedToolContent? result = resBytes.HasValue && resChars.HasValue
            ? new CollectedToolContent(resText, resBytes.Value, resChars.Value, null, resTrunc)
            : null;

        long? succeededRaw = ParseNullableInt64(cols[startIndex + 24], cols[startIndex + 25], "tool_call_outcome.succeeded");
        bool? succeeded = succeededRaw.HasValue
            ? succeededRaw.Value switch
            {
                0L => false,
                1L => true,
                _ => throw new StoredDataException($"Invalid boolean flag '{succeededRaw.Value.ToString(CultureInfo.InvariantCulture)}' in tool_call_outcome.succeeded."),
            }
            : null;

        string? errorMsg = ParseNullableHex(cols[startIndex + 26], cols[startIndex + 27], "tool_call_error.message");
        string? errorCode = ParseNullableHex(cols[startIndex + 28], cols[startIndex + 29], "tool_call_error_code.code");
        CollectedError? error = errorMsg is not null ? new CollectedError(errorMsg, errorCode) : null;

        long? exitCode = ParseNullableInt64(cols[startIndex + 30], cols[startIndex + 31], "tool_call_exit_code.exit_code");
        return new CollectedToolCall(callId, toolName, mcpServer, mcpTool, input, result, succeeded, error, exitCode);
    }

    private static string BuildAgentInvocationSelect(string alias, string whereClause, string orderBy) =>
        $"SELECT hex(CAST({alias}.span_id AS BLOB)), {BuildAgentInvocationColumns(alias)} " +
        $"FROM agent_invocation {alias} " +
        $"LEFT JOIN agent_invocation_spawn ais ON ais.span_id = {alias}.span_id " +
        $"LEFT JOIN agent_invocation_outcome aio ON aio.span_id = {alias}.span_id " +
        $"LEFT JOIN agent_invocation_error aie ON aie.span_id = {alias}.span_id" +
        $"{whereClause} {orderBy};\n";

    private static string BuildAgentInvocationColumns(string alias) =>
        $"hex(CAST({alias}.agent_name AS BLOB)), " +
        "typeof(ais.spawning_record_id), COALESCE(hex(CAST(ais.spawning_record_id AS BLOB)), ''), " +
        "typeof(aio.succeeded), COALESCE(CAST(aio.succeeded AS TEXT), ''), " +
        "typeof(aie.message), COALESCE(hex(CAST(aie.message AS BLOB)), '')";

    private static CollectedAgentInvocation ParseAgentInvocationColumns(string[] cols, int startIndex)
    {
        // Total = 1 + 6 = 7 columns.
        string agentName = ParseHexRequired(cols[startIndex], "agent_invocation.agent_name");
        string? spawnId = ParseNullableHex(cols[startIndex + 1], cols[startIndex + 2], "agent_invocation_spawn.spawning_record_id");
        long? succeededRaw = ParseNullableInt64(cols[startIndex + 3], cols[startIndex + 4], "agent_invocation_outcome.succeeded");
        bool? succeeded = succeededRaw.HasValue
            ? succeededRaw.Value switch
            {
                0L => false,
                1L => true,
                _ => throw new StoredDataException($"Invalid boolean flag '{succeededRaw.Value.ToString(CultureInfo.InvariantCulture)}' in agent_invocation_outcome.succeeded."),
            }
            : null;
        string? error = ParseNullableHex(cols[startIndex + 5], cols[startIndex + 6], "agent_invocation_error.message");
        return new CollectedAgentInvocation(agentName, spawnId, succeeded, error);
    }

    private static string? BuildStoredFieldsScopeWhere(TableDefinition table, string sessionExpr)
    {
        string tableName = table.Name;
        if (string.Equals(tableName, "harness", StringComparison.Ordinal) ||
            tableName.StartsWith("harness_rollup", StringComparison.Ordinal))
        {
            return $"harness_id IN (SELECT harness_id FROM session WHERE session_id = {sessionExpr} UNION SELECT r.harness_id FROM record r JOIN record_session_link rsl ON rsl.span_id = r.span_id WHERE rsl.session_key = {sessionExpr})";
        }

        if (string.Equals(tableName, "source", StringComparison.Ordinal))
        {
            return $"source_id IN (SELECT DISTINCT r.source_id FROM record r JOIN record_session_link rsl ON rsl.span_id = r.span_id WHERE rsl.session_key = {sessionExpr})";
        }

        if (string.Equals(tableName, "session", StringComparison.Ordinal) ||
            tableName.StartsWith("session_", StringComparison.Ordinal))
        {
            return table.Columns.Any(static c => string.Equals(c.Name, "session_id", StringComparison.Ordinal))
                ? $"session_id = {sessionExpr}"
                : null;
        }

        if (tableName.StartsWith("run", StringComparison.Ordinal))
        {
            return $"run_id IN (SELECT DISTINCT e.run_id FROM execution e JOIN execution_session es ON es.execution_id = e.execution_id WHERE es.session_id = {sessionExpr})";
        }

        if (tableName.StartsWith("execution", StringComparison.Ordinal))
        {
            return $"execution_id IN (SELECT execution_id FROM execution_session WHERE session_id = {sessionExpr})";
        }

        if (tableName.StartsWith("finding", StringComparison.Ordinal))
        {
            return $"finding_id IN (SELECT finding_id FROM finding_session WHERE session_id = {sessionExpr})";
        }

        if (tableName.StartsWith("prediction", StringComparison.Ordinal))
        {
            return $"prediction_id IN (SELECT p.prediction_id FROM prediction p WHERE p.finding_id IN (SELECT finding_id FROM finding_session WHERE session_id = {sessionExpr}) OR p.run_id IN (SELECT e.run_id FROM execution e JOIN execution_session es ON es.execution_id = e.execution_id WHERE es.session_id = {sessionExpr}))";
        }

        if (tableName.StartsWith("ingest_problem", StringComparison.Ordinal))
        {
            return $"problem_id IN (SELECT problem_id FROM ingest_problem_session WHERE session_id = {sessionExpr})";
        }

        if (string.Equals(tableName, "projection_dirty_session", StringComparison.Ordinal))
        {
            return $"session_id = {sessionExpr}";
        }

        if (table.Columns.Any(static c => string.Equals(c.Name, "span_id", StringComparison.Ordinal)))
        {
            return $"span_id IN (SELECT span_id FROM record_session_link WHERE session_key = {sessionExpr})";
        }

        return null;
    }

    /// <summary>Appends the <c>typeof</c> tag and value expression pair one stored column is read with.</summary>
    private static void AppendStoredFieldValueSelect(StringBuilder sql, TableDefinition table, ColumnDefinition column)
    {
        switch (column.Type)
        {
            case SqliteColumnType.SqliteText:
                sql.Append(CultureInfo.InvariantCulture, $", typeof({column.Name}), COALESCE(hex(CAST({column.Name} AS BLOB)), '')");
                break;
            case SqliteColumnType.SqliteInteger:
            case SqliteColumnType.SqliteReal:
                sql.Append(CultureInfo.InvariantCulture, $", typeof({column.Name}), COALESCE(CAST({column.Name} AS TEXT), '')");
                break;
            default:
                throw new StoredDataException($"Unsupported catalog column type '{column.Type}' on '{table.Name}.{column.Name}'.");
        }
    }

    /// <summary>
    /// The key columns each table's missing-parent-key computation reads: a qualifying child (one
    /// foreign key whose columns are its primary key) reads its own key columns, and the parent
    /// such a child references reads the referenced columns. Every other column stays unread.
    /// </summary>
    private static Dictionary<string, List<ColumnDefinition>> StoredFieldsKeyColumns(IReadOnlyList<TableDefinition> tables)
    {
        Dictionary<string, HashSet<string>> keyNamesByTable = new(tables.Count, StringComparer.Ordinal);
        foreach (TableDefinition table in tables)
        {
            keyNamesByTable[table.Name] = new HashSet<string>(StringComparer.Ordinal);
        }

        foreach (TableDefinition child in tables)
        {
            if (child.ForeignKeys.Count != 1 ||
                !child.ForeignKeys[0].Columns.SequenceEqual(child.PrimaryKeyColumns, StringComparer.Ordinal))
            {
                continue;
            }

            ForeignKeyDefinition foreignKey = child.ForeignKeys[0];
            HashSet<string> childKeys = keyNamesByTable[child.Name];
            foreach (string column in foreignKey.Columns)
            {
                childKeys.Add(column);
            }

            HashSet<string> parentKeys = keyNamesByTable[RelationalSchemaCatalog.GetTable(foreignKey.ReferencedTable).Name];
            foreach (string column in foreignKey.ReferencedColumns)
            {
                parentKeys.Add(column);
            }
        }

        Dictionary<string, List<ColumnDefinition>> keyColumnsByTable = new(tables.Count, StringComparer.Ordinal);
        foreach (TableDefinition table in tables)
        {
            keyColumnsByTable[table.Name] =
                [.. table.Columns.Where(column => keyNamesByTable[table.Name].Contains(column.Name))];
        }

        return keyColumnsByTable;
    }

    /// <summary>Parses one row's <c>typeof</c>/value pairs, in catalog column order, into a row.</summary>
    private static StoredFieldRow ParseStoredFieldRow(TableDefinition table, string[] pairs)
    {
        if (pairs.Length != table.Columns.Count * 2)
        {
            throw new StoredDataException(
                $"Expected {(table.Columns.Count * 2).ToString(CultureInfo.InvariantCulture)} value columns for stored-fields table '{table.Name}', got {pairs.Length.ToString(CultureInfo.InvariantCulture)}.");
        }

        List<StoredFieldCell> cells = new(table.Columns.Count);
        for (int colIndex = 0; colIndex < table.Columns.Count; colIndex++)
        {
            ColumnDefinition column = table.Columns[colIndex];
            cells.Add(ParseStoredFieldCell(table.Name, column, pairs[colIndex * 2], pairs[(colIndex * 2) + 1]));
        }

        return new StoredFieldRow(cells);
    }

    private static StoredFieldCell ParseStoredFieldCell(
        string tableName,
        ColumnDefinition column,
        string typeCell,
        string valCell)
    {
        if (string.Equals(typeCell, "null", StringComparison.Ordinal))
        {
            return new StoredFieldCell(column.Name, column.Type);
        }

        string site = $"{tableName}.{column.Name}";
        return column.Type switch
        {
            SqliteColumnType.SqliteText => string.Equals(typeCell, "text", StringComparison.Ordinal)
                ? new StoredFieldCell(column.Name, column.Type, TextValue: ParseHexRequired(valCell, site))
                : throw new StoredDataException($"Unexpected SQLite storage type '{typeCell}' for text column '{site}'."),
            SqliteColumnType.SqliteInteger => string.Equals(typeCell, "integer", StringComparison.Ordinal)
                ? new StoredFieldCell(column.Name, column.Type, IntegerValue: ParseInt64Required(valCell, site))
                : throw new StoredDataException($"Unexpected SQLite storage type '{typeCell}' for integer column '{site}'."),
            SqliteColumnType.SqliteReal => typeCell is "real" or "integer"
                ? new StoredFieldCell(column.Name, column.Type, RealValue: ParseDoubleRequired(valCell, site))
                : throw new StoredDataException($"Unexpected SQLite storage type '{typeCell}' for real column '{site}'."),
            _ => throw new StoredDataException($"Unsupported column type '{column.Type}' on '{site}'."),
        };
    }

    private static List<string> ComputeStoredFieldsMissingParentKeys(
        TableDefinition table,
        List<StoredFieldRow> childRows,
        IReadOnlyDictionary<string, List<StoredFieldRow>> rowsByTable)
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

        if (!rowsByTable.TryGetValue(fk.ReferencedTable, out List<StoredFieldRow>? parentRows) || parentRows.Count == 0)
        {
            return [];
        }

        TableDefinition parentTable = RelationalSchemaCatalog.GetTable(fk.ReferencedTable);
        int[] childKeyIndices = ResolveColumnIndices(table, fk.Columns);
        int[] parentKeyIndices = ResolveColumnIndices(parentTable, fk.ReferencedColumns);

        HashSet<string> presentChildKeys = new(StringComparer.Ordinal);
        foreach (StoredFieldRow childRow in childRows)
        {
            _ = presentChildKeys.Add(FormatStoredRowKey(childRow, childKeyIndices));
        }

        List<string> missing = [];
        foreach (StoredFieldRow parentRow in parentRows)
        {
            string key = FormatStoredRowKey(parentRow, parentKeyIndices);
            if (!presentChildKeys.Contains(key))
            {
                missing.Add(key);
            }
        }

        return missing;
    }

    private static int[] ResolveColumnIndices(TableDefinition table, IReadOnlyList<string> columnNames)
    {
        int[] indices = new int[columnNames.Count];
        for (int index = 0; index < columnNames.Count; index++)
        {
            string target = columnNames[index];
            int found = -1;
            for (int colIndex = 0; colIndex < table.Columns.Count; colIndex++)
            {
                if (string.Equals(table.Columns[colIndex].Name, target, StringComparison.Ordinal))
                {
                    found = colIndex;
                    break;
                }
            }

            if (found < 0)
            {
                throw new StoredDataException($"Column '{target}' not found on table '{table.Name}'.");
            }

            indices[index] = found;
        }

        return indices;
    }

    private static string FormatStoredRowKey(StoredFieldRow row, int[] keyIndices)
    {
        if (keyIndices.Length == 1)
        {
            return FormatStoredCellValue(row.Cells[keyIndices[0]]);
        }

        string[] parts = new string[keyIndices.Length];
        for (int index = 0; index < keyIndices.Length; index++)
        {
            parts[index] = FormatStoredCellValue(row.Cells[keyIndices[index]]);
        }

        return string.Join(":", parts);
    }

    private static string FormatStoredCellValue(StoredFieldCell cell) => cell.ColumnType switch
    {
        SqliteColumnType.SqliteText => cell.TextValue ?? string.Empty,
        SqliteColumnType.SqliteInteger => cell.IntegerValue.HasValue
            ? cell.IntegerValue.Value.ToString(CultureInfo.InvariantCulture)
            : string.Empty,
        SqliteColumnType.SqliteReal => cell.RealValue.HasValue
            ? cell.RealValue.Value.ToString(CultureInfo.InvariantCulture)
            : string.Empty,
        _ => string.Empty,
    };

    private static List<(int Position, CollectedRecord Record)> ResolveSessionTurnEntriesForReader(
        IReadOnlyList<StoredSessionTurn> projectedTurns,
        IReadOnlyList<CollectedRecord> records)
    {
        Dictionary<string, CollectedRecord> bySpan = new(records.Count, StringComparer.Ordinal);
        foreach (CollectedRecord record in records)
        {
            bySpan[record.RecordId] = record;
        }

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

    private static ReadTimeTurnSummary BuildReaderTurnSummary(
        CollectedRecord record,
        int position,
        string harnessId,
        IModelPricingLookup? pricing,
        out CostBlock publishedBlock,
        out CostBlock? harnessBlock)
    {
        long? freshInput = TryFindReaderTokenCount(record.Tokens, TokenClass.Input);
        long? cacheRead = TryFindReaderTokenCount(record.Tokens, TokenClass.CacheRead);
        long? cacheCreation = TryFindReaderTokenCount(record.Tokens, TokenClass.CacheWrite);
        long? output = TryFindReaderTokenCount(record.Tokens, TokenClass.Output);
        long? reasoning = TryFindReaderTokenCount(record.Tokens, TokenClass.Reasoning);

        long? inputTokens = freshInput.HasValue || cacheRead.HasValue || cacheCreation.HasValue
            ? (freshInput ?? 0L) + (cacheRead ?? 0L) + (cacheCreation ?? 0L)
            : null;

        long? durationMs = ComputeReaderDurationMs(record);

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
            reportedCost = ToReaderCostSummary(harnessBlock);
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
            PublishedCost: ToReaderCostSummary(publishedBlock));
    }

    private static long? TryFindReaderTokenCount(IReadOnlyList<CollectedTokenCount> tokens, TokenClass tokenClass)
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

    private static long? ComputeReaderDurationMs(CollectedRecord record)
    {
        if (record.EndedAt.HasValue)
        {
            return Math.Max(0L, (long)Math.Round((record.EndedAt.Value - record.StartedAt).TotalMilliseconds));
        }

        return record.ReportedDurationMs;
    }

    private static ReadTimeCostSummary ToReaderCostSummary(CostBlock block)
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

    private static ReadTimeCostSummary AggregateReaderCostBlocks(
        IReadOnlyList<CostBlock> blocks,
        CostBasis fallbackBasis,
        List<Problem> problems)
    {
        CostTotalResult result = CanonPricing.SumCosts(blocks);
        if (result.Ok && result.Total is not null)
        {
            return ToReaderCostSummary(result.Total);
        }

        if (result.Problem is not null)
        {
            problems.Add(result.Problem);
        }

        return new ReadTimeCostSummary(fallbackBasis, CostStatus.Partial, null, null, []);
    }

    private static List<ReadTimeSessionToolSummary> BuildReaderToolSummaries(
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

    private static List<ReadTimeSessionServerSummary> BuildReaderServerSummaries(
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

    private static List<ReadTimeSessionRequestSummary> BuildReaderRequestSummaries(
        IReadOnlyList<string> requestSpanIds,
        IReadOnlyList<CollectedRecord> records,
        IReadOnlyDictionary<string, CollectedRecord> recordsBySpanId,
        IReadOnlyList<(int Position, CollectedRecord Record)> turnEntries)
    {
        List<ReadTimeSessionRequestSummary> summaries = new(requestSpanIds.Count);
        foreach (string requestSpanId in requestSpanIds)
        {
            HashSet<string> subtree = CollectReaderDescendantSpanIds(requestSpanId, records);
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

            string? promptPreview = ExtractReaderPromptPreview(requestRecord, ownedTurns);
            summaries.Add(new ReadTimeSessionRequestSummary(
                RequestSpanId: requestSpanId,
                TimestampUtc: timestampUtc,
                Turns: ownedTurns.Count,
                Model: model,
                PromptPreview: promptPreview));
        }

        return summaries;
    }

    private static HashSet<string> CollectReaderDescendantSpanIds(string rootSpanId, IReadOnlyList<CollectedRecord> records)
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

    private static string? ExtractReaderPromptPreview(
        CollectedRecord? requestRecord,
        IReadOnlyList<CollectedRecord> ownedTurns)
    {
        if (requestRecord is not null && HasReaderPromptParts(requestRecord.Parts))
        {
            return ExtractReaderPromptFromParts(requestRecord.Parts);
        }

        foreach (CollectedRecord ownedTurn in ownedTurns)
        {
            if (HasReaderPromptParts(ownedTurn.Parts))
            {
                return ExtractReaderPromptFromParts(ownedTurn.Parts);
            }
        }

        return null;
    }

    private static bool HasReaderPromptParts(IReadOnlyList<CollectedContentPart> parts)
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

    private static string? ExtractReaderPromptFromParts(IReadOnlyList<CollectedContentPart> parts)
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

                return TruncateReaderSurrogateSafe(trimmed, 200);
            }
        }

        return null;
    }

    private static string TruncateReaderSurrogateSafe(string text, int maxLength)
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

    private static List<SubagentSession> BuildReaderSubagentSummaries(
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

    private static ReadTimeAuxiliarySummary BuildReaderAuxiliarySummary(
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
                    .Where(static r => IsReaderAuxiliaryRecord(r))
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

            long? freshInput = TryFindReaderTokenCount(record.Tokens, TokenClass.Input);
            long? cacheRead = TryFindReaderTokenCount(record.Tokens, TokenClass.CacheRead);
            long? cacheCreation = TryFindReaderTokenCount(record.Tokens, TokenClass.CacheWrite);
            long? output = TryFindReaderTokenCount(record.Tokens, TokenClass.Output);
            long? reasoning = TryFindReaderTokenCount(record.Tokens, TokenClass.Reasoning);
            publishedBlocks.Add(PriceReportedTokens(freshInput, cacheRead, cacheCreation, output, reasoning, record, harnessId, pricing));
        }

        ReadTimeCostSummary? reportedCost = reportedBlocks.Count > 0
            ? AggregateReaderCostBlocks(reportedBlocks, CostBasis.Harness, problems)
            : null;
        ReadTimeCostSummary publishedCost = publishedBlocks.Count > 0
            ? AggregateReaderCostBlocks(publishedBlocks, CostBasis.Published, problems)
            : new ReadTimeCostSummary(CostBasis.Unknown, CostStatus.NoRate, null, null, []);

        return new ReadTimeAuxiliarySummary(spanIds, reportedCost, publishedCost);
    }

    private static bool IsReaderAuxiliaryRecord(CollectedRecord record)
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

    private static ReadTimeContextBucketSummary BuildReaderContextBucketSummary(
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

    private static UnusedSchemaRange? ResolveReaderUnusedRange(
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

    private static string[] SplitTabLine(string line, int expectedColumns, string context)
    {
        string[] cols = line.Split('\t');
        if (cols.Length != expectedColumns)
        {
            throw new StoredDataException(
                $"Expected {expectedColumns.ToString(CultureInfo.InvariantCulture)} columns for {context}, got {cols.Length.ToString(CultureInfo.InvariantCulture)}.");
        }

        return cols;
    }

    private static string ParseHexRequired(string hexCell, string columnName)
    {
        try
        {
            return DecodeHexText(hexCell);
        }
        catch (FormatException ex)
        {
            throw new StoredDataException($"Invalid hexadecimal UTF-8 payload in '{columnName}': '{hexCell}'.", ex);
        }
    }

    private static string? ParseNullableHex(string typeCell, string hexCell, string columnName)
    {
        if (string.Equals(typeCell, "null", StringComparison.Ordinal))
        {
            return null;
        }

        if (string.Equals(typeCell, "text", StringComparison.Ordinal))
        {
            return ParseHexRequired(hexCell, columnName);
        }

        throw new StoredDataException($"Unexpected SQLite storage type '{typeCell}' for nullable text column '{columnName}'.");
    }

    private static long ParseInt64Required(string cell, string columnName)
    {
        if (long.TryParse(cell, NumberStyles.Integer, CultureInfo.InvariantCulture, out long value))
        {
            return value;
        }

        throw new StoredDataException($"Invalid integer value '{cell}' for '{columnName}'.");
    }

    private static long? ParseNullableInt64(string typeCell, string valCell, string columnName)
    {
        if (string.Equals(typeCell, "null", StringComparison.Ordinal))
        {
            return null;
        }

        if (string.Equals(typeCell, "integer", StringComparison.Ordinal))
        {
            return ParseInt64Required(valCell, columnName);
        }

        throw new StoredDataException($"Unexpected SQLite storage type '{typeCell}' for nullable integer column '{columnName}'.");
    }

    private static int ParseInt32Required(string cell, string columnName)
    {
        if (int.TryParse(cell, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value))
        {
            return value;
        }

        throw new StoredDataException($"Invalid 32-bit integer value '{cell}' for '{columnName}'.");
    }

    private static bool ParseBoolFlag(string cell, string columnName) => cell switch
    {
        "0" => false,
        "1" => true,
        _ => throw new StoredDataException($"Invalid boolean flag '{cell}' for '{columnName}'."),
    };

    /// <summary>Parses a nullable boolean stored as an integer, absent when the column is null.</summary>
    private static bool? ParseOptionalBool(string typeCell, string valCell, string columnName) => typeCell switch
    {
        "null" => null,
        "integer" => valCell switch
        {
            "0" => false,
            "1" => true,
            _ => throw new StoredDataException($"Invalid boolean flag '{valCell}' for '{columnName}'."),
        },
        _ => throw new StoredDataException($"Unexpected SQLite storage type '{typeCell}' for nullable boolean column '{columnName}'."),
    };

    private static double ParseDoubleRequired(string cell, string columnName)
    {
        if (double.TryParse(cell, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out double value) &&
            double.IsFinite(value))
        {
            return value;
        }

        throw new StoredDataException($"Invalid finite floating-point value '{cell}' for '{columnName}'.");
    }

    private static double? ParseNullableDouble(string typeCell, string valCell, string columnName)
    {
        if (string.Equals(typeCell, "null", StringComparison.Ordinal))
        {
            return null;
        }

        if (typeCell is "real" or "integer")
        {
            return ParseDoubleRequired(valCell, columnName);
        }

        throw new StoredDataException($"Unexpected SQLite storage type '{typeCell}' for nullable real column '{columnName}'.");
    }

    private static DateTimeOffset ParseTimestampRequired(string text, string columnName)
    {
        if (DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTimeOffset parsed))
        {
            return parsed.ToUniversalTime();
        }

        throw new StoredDataException($"Invalid ISO-8601 timestamp '{text}' for '{columnName}'.");
    }

    private static SourceKind ParseSourceKind(string value) => value switch
    {
        "file" => SourceKind.File,
        "otlp" => SourceKind.Otlp,
        _ => throw new StoredDataException($"Unrecognized source_kind '{value}'."),
    };

    private static RecordOp ParseRecordOp(string value) => value switch
    {
        "llm.invoke" => RecordOp.LlmInvoke,
        "tool.invoke" => RecordOp.ToolInvoke,
        "agent.invoke" => RecordOp.AgentInvoke,
        "unspecified" => RecordOp.Unspecified,
        _ => throw new StoredDataException($"Unrecognized record.op '{value}'."),
    };

    private static RecordKind ParseRecordKind(string value) => value switch
    {
        "unspecified" => RecordKind.Unspecified,
        "internal" => RecordKind.Internal,
        "server" => RecordKind.Server,
        "client" => RecordKind.Client,
        "producer" => RecordKind.Producer,
        "consumer" => RecordKind.Consumer,
        _ => throw new StoredDataException($"Unrecognized record.kind '{value}'."),
    };

    private static RecordStatus ParseRecordStatus(string value) => value switch
    {
        "unset" => RecordStatus.Unset,
        "ok" => RecordStatus.Ok,
        "error" => RecordStatus.Error,
        "unspecified" => RecordStatus.Unspecified,
        _ => throw new StoredDataException($"Unrecognized record.status '{value}'."),
    };

    private static TokenClass ParseTokenClass(string value) => value switch
    {
        "input" => TokenClass.Input,
        "cache_read" => TokenClass.CacheRead,
        "cache_write" => TokenClass.CacheWrite,
        "output" => TokenClass.Output,
        "reasoning" => TokenClass.Reasoning,
        "total" => TokenClass.Total,
        _ => throw new StoredDataException($"Unrecognized token_class '{value}'."),
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
        _ => throw new StoredDataException($"Unrecognized content_part.bucket '{value}'."),
    };

    private static ReportedTotalDirection ParseReportedDirection(string value) => value switch
    {
        "input" => ReportedTotalDirection.Input,
        "output" => ReportedTotalDirection.Output,
        _ => throw new StoredDataException($"Unrecognized record_reported_total.direction '{value}'."),
    };

    private static ProblemSeverity ParseProblemSeverityValue(string value)
    {
        if (string.Equals(value, "error", StringComparison.OrdinalIgnoreCase))
        {
            return ProblemSeverity.Error;
        }

        if (string.Equals(value, "warning", StringComparison.OrdinalIgnoreCase))
        {
            return ProblemSeverity.Warning;
        }

        throw new StoredDataException($"Unrecognized ingest_problem.severity '{value}'.");
    }

    private static List<T> GetOrCreateList<TKey, T>(Dictionary<TKey, List<T>> map, TKey key)
        where TKey : notnull
    {
        if (!map.TryGetValue(key, out List<T>? list))
        {
            list = [];
            map[key] = list;
        }

        return list;
    }

    /// <summary>
    /// Prices a turn from the classes and reported totals that were stored.
    /// The rule lives in <see cref="CanonPricing.PriceStoredTokens"/> so the
    /// in-memory store prices the same way.
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

    private record RawRecordHeader(
        string SpanId,
        string HarnessId,
        RecordOp Op,
        RecordKind Kind,
        string Name,
        DateTimeOffset StartedAt,
        RecordStatus Status,
        string? SessionKey,
        string? ParentSpanId,
        string? TraceId,
        string? Model,
        DateTimeOffset? EndedAt,
        long? ReportedDurationMs,
        CollectedCost? ReportedCost,
        string? AgentInstanceId,
        CollectedRecordSignals Signals,
        long? ReportedInputTokens,
        long? ReportedOutputTokens,
        string SourceName,
        string? ResponseId,
        string? RequestId);

    private record RawSessionHeader(
        string SessionId,
        string HarnessId,
        string NativeId,
        CollectedWorkspace? Workspace,
        string? Title);

    /// <summary>
    /// What a session's list figure takes from its projection: the number of request spans, which
    /// a figure prefers over its record-derived request count, and the stored turn entries.
    /// </summary>
    private record SessionListProjectionShape(
        int RequestSpanCount,
        IReadOnlyList<StoredSessionTurn> Turns);

    private record RawContextHeader(
        long ContextLimit,
        string ContextLimitSource,
        long ResidualTotal,
        double FreshJumpFactor,
        double AccumulationRate);

    private record RawFindingHeader(
        string FindingId,
        string DetectorId,
        string Title,
        string Mechanism,
        string Confidence,
        string Recommendation,
        string OutcomeRiskCaveat,
        double RankScore,
        long? EstimatedWasteTokens,
        FindingErrorBar? ErrorBar,
        string? RunId,
        string? SessionId,
        string? MeasurementClass,
        string? ConfidenceBasis,
        string? WhatWouldRaiseIt,
        StoredCompactionHazard? CompactionHazard);

    private record RawContextTurnHeader(
        int Position,
        long InputTokens,
        long BucketedTokens,
        long ResidualTokens,
        string ResidualAttribution,
        long BuiltinToolDefinitionTokens,
        long FreshInput,
        long? Headroom,
        double? Pressure,
        double? AccumulationRate,
        long? StrippedBlockCount,
        long? StrippedTokens,
        long? PreviousFreshInput,
        double? FreshJumpFactor,
        long? NonResidentTokens);
}
