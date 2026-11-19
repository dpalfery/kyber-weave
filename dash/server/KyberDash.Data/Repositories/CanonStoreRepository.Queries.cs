using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using KyberDash.Domain;

namespace KyberDash.Data.Repositories;

/// <summary>Read-side SQL and row mapping for <see cref="CanonStoreRepository"/>.</summary>
public partial class CanonStoreRepository
{
    // A stored value is untrusted: a corrupt row must not abort the whole listing, so each read
    // that cannot be represented falls back to the DTO's zero or absent value.
    private static int IntOrZero(string text) =>
        int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value) ? value : 0;

    private static int? IntOrNull(string text) =>
        int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value) ? value : null;

    private static long LongOrZero(string text) =>
        long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out long value) ? value : 0;

    private static long? LongOrNull(string text) =>
        long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out long value) ? value : null;

    private static double DoubleOrZero(string text) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double value) && double.IsFinite(value)
            ? value
            : 0d;

    /// <summary>
    /// Decodes one stored column given its <c>typeof()</c> marker: a zlib blob when the marker says
    /// <c>blob</c>, otherwise hex-encoded text. Returns <see langword="false"/> for tampered hex or a
    /// blob that is not zlib, so the caller can skip that row instead of failing the read.
    /// </summary>
    private static bool TryDecodeStoredColumn(string typeMarker, string hex, out string text)
    {
        try
        {
            text = string.Equals(typeMarker, "blob", StringComparison.Ordinal)
                ? CanonCompressedColumn.Decompress(Convert.FromHexString(hex))
                : DecodeHexText(hex);
            return true;
        }
        catch (FormatException)
        {
            text = string.Empty;
            return false;
        }
        catch (InvalidDataException)
        {
            text = string.Empty;
            return false;
        }
    }

    /// <summary>
    /// Decodes a stored <c>content_json</c> column into the record's string map.
    /// </summary>
    /// <remarks>
    /// The column is an object of recorded parts, but its values are untrusted: a writer that
    /// predates this engine recorded span attribute values verbatim, so a boolean, a number, an
    /// object, or an array sits beside the text. Every value is preserved as text (a scalar's JSON
    /// literal, a container's JSON text) because a row that cannot be represented must not abort
    /// the listing. A column that is not a JSON object, or not JSON at all, reads as empty.
    /// </remarks>
    /// <param name="json">The column text as stored.</param>
    /// <returns>The decoded content, never <see langword="null"/>.</returns>
    internal static Dictionary<string, string> ParseContentColumn(string json)
    {
        Dictionary<string, string> content = new(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(json))
            return content;

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            return content;
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                return content;

            foreach (JsonProperty property in document.RootElement.EnumerateObject())
            {
                content[property.Name] = property.Value.ValueKind switch
                {
                    JsonValueKind.String => property.Value.GetString() ?? string.Empty,
                    JsonValueKind.Null or JsonValueKind.Undefined => string.Empty,
                    _ => property.Value.GetRawText(),
                };
            }
        }

        return content;
    }

    private IReadOnlyList<CanonicalRecord> QueryRecords(string whereAndOrderClause)
    {
        string output = ExecuteSqlite(
            ".mode tabs\n" +
            ".headers off\n" +
            "SELECT " +
            "COALESCE(hex(CAST(span_id AS BLOB)), '2d'), " +
            "typeof(trace_id), COALESCE(hex(CAST(trace_id AS BLOB)), ''), " +
            "typeof(parent_span_id), COALESCE(hex(CAST(parent_span_id AS BLOB)), ''), " +
            "hex(CAST(source AS BLOB)), " +
            "hex(CAST(harness AS BLOB)), " +
            "typeof(session_id), COALESCE(hex(CAST(session_id AS BLOB)), ''), " +
            "hex(CAST(name AS BLOB)), " +
            "hex(CAST(op AS BLOB)), " +
            "hex(CAST(kind AS BLOB)), " +
            "hex(CAST(timestamp AS BLOB)), " +
            "duration_ms, " +
            "hex(CAST(status AS BLOB)), " +
            "hex(CAST(tokens_json AS BLOB)), " +
            "hex(CAST(content_json AS BLOB)), " +
            "hex(CAST(cost_json AS BLOB)), " +
            "typeof(measurability_json), COALESCE(hex(CAST(measurability_json AS BLOB)), ''), " +
            "typeof(parts_json), COALESCE(hex(CAST(parts_json AS BLOB)), ''), " +
            "COALESCE(hex(raw), ''), typeof(raw) " +
            $"FROM records {whereAndOrderClause};");

        List<CanonicalRecord> rows = [];
        foreach (string line in TabLines(output))
        {
            string[] cols = line.Split('\t', StringSplitOptions.None);
            if (cols.Length < 24)
                continue;

            rows.Add(ParseRecordRow(cols));
        }

        return rows;
    }

    private static CanonicalRecord ParseRecordRow(string[] cols)
    {
        string recordSpanId = DecodeHexText(cols[0]);
        string? traceId = cols[1] == "null" ? null : DecodeHexText(cols[2]);
        string? parentSpanId = cols[3] == "null" ? null : DecodeHexText(cols[4]);
        string source = DecodeHexText(cols[5]);
        string harness = DecodeHexText(cols[6]);
        string? sessionId = cols[7] == "null" ? null : DecodeHexText(cols[8]);
        string name = DecodeHexText(cols[9]);
        string op = DecodeHexText(cols[10]);
        string kind = DecodeHexText(cols[11]);
        string timestamp = DecodeHexText(cols[12]);
        long durationMs = LongOrZero(cols[13]);
        string status = DecodeHexText(cols[14]);
        TokenUsage tokens = JsonSerializer.Deserialize<TokenUsage>(DecodeHexText(cols[15]), RecordJsonOptions)
            ?? throw new InvalidOperationException($"corrupt tokens_json for span {recordSpanId}.");
        Dictionary<string, string> storedContent = ParseContentColumn(DecodeHexText(cols[16]));
        CostBlock cost = JsonSerializer.Deserialize<CostBlock>(DecodeHexText(cols[17]), RecordJsonOptions)
            ?? throw new InvalidOperationException($"corrupt cost_json for span {recordSpanId}.");

        IReadOnlyDictionary<string, JsonElement>? measurability = cols[18] == "null"
            ? null
            : JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(DecodeHexText(cols[19]), RecordJsonOptions);

        IReadOnlyList<ContentPart>? parts = null;
        if (cols[20] != "null" && TryDecodeStoredColumn(cols[20], cols[21], out string partsJson))
            parts = JsonSerializer.Deserialize<List<ContentPart>>(partsJson, RecordJsonOptions);

        IReadOnlyDictionary<string, string> content = parts is null
            ? storedContent
            : MergeContent(CanonValidation.ContentFromParts(parts), storedContent);

        IReadOnlyDictionary<string, JsonElement>? raw = null;
        if (cols[23] != "null" && TryDecodeStoredColumn(cols[23], cols[22], out string rawJson))
            raw = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(rawJson, RecordJsonOptions);

        return new CanonicalRecord
        {
            SpanId = recordSpanId,
            TraceId = traceId,
            ParentSpanId = parentSpanId,
            Source = source,
            Harness = harness,
            SessionId = sessionId,
            Name = name,
            Op = op,
            Kind = kind,
            Timestamp = timestamp,
            DurationMs = durationMs,
            Status = status,
            Tokens = tokens,
            Content = content,
            Parts = parts,
            Cost = cost,
            Measurability = measurability,
            Raw = raw,
        };
    }

    private static Dictionary<string, string> MergeContent(
        IReadOnlyDictionary<string, string> derived,
        IReadOnlyDictionary<string, string> stored)
    {
        Dictionary<string, string> merged = new(derived.Count + stored.Count, StringComparer.Ordinal);
        foreach (KeyValuePair<string, string> entry in derived)
            merged[entry.Key] = entry.Value;
        foreach (KeyValuePair<string, string> entry in stored)
            merged[entry.Key] = entry.Value;
        return merged;
    }

    private IReadOnlyList<SessionSummaryRow> QuerySessions(string suffixSql, string? singleSessionId)
    {
        string output = ExecuteSqlite(
            ".mode tabs\n" +
            ".headers off\n" +
            "SELECT " +
            "hex(CAST(session_id AS BLOB)), " +
            "hex(CAST(harness AS BLOB)), " +
            "typeof(label), COALESCE(hex(CAST(label AS BLOB)), ''), " +
            "is_subagent, " +
            "typeof(parent_session), COALESCE(hex(CAST(parent_session AS BLOB)), ''), " +
            "typeof(agent_name), COALESCE(hex(CAST(agent_name AS BLOB)), ''), " +
            "typeof(repo), COALESCE(hex(CAST(repo AS BLOB)), ''), " +
            "typeof(branch), COALESCE(hex(CAST(branch AS BLOB)), ''), " +
            "typeof(started), COALESCE(hex(CAST(started AS BLOB)), ''), " +
            "typeof(ended), COALESCE(hex(CAST(ended AS BLOB)), ''), " +
            "typeof(summary_json), COALESCE(hex(CAST(summary_json AS BLOB)), ''), " +
            "typeof(problems_count), COALESCE(problems_count, 0), " +
            "typeof(context_limit_source), COALESCE(hex(CAST(context_limit_source AS BLOB)), ''), " +
            "hex(CAST(payload AS BLOB)) " +
            $"FROM session {suffixSql};");

        bool hasTurnTable = HasSessionTurnTable();
        Dictionary<string, List<JsonElement>>? turnsBySession = hasTurnTable
            ? ReadSessionTurnsMap(singleSessionId)
            : null;

        List<SessionSummaryRow> rows = [];
        foreach (string line in TabLines(output))
        {
            string[] cols = line.Split('\t', StringSplitOptions.None);
            if (cols.Length < 24)
                continue;

            string sessionId = DecodeHexText(cols[0]);
            string harness = DecodeHexText(cols[1]);
            string? label = cols[2] == "null" ? null : DecodeHexText(cols[3]);
            bool isSubagent = cols[4] != "0";
            string? parentSession = cols[5] == "null" ? null : DecodeHexText(cols[6]);
            string? agentName = cols[7] == "null" ? null : DecodeHexText(cols[8]);
            string? repo = cols[9] == "null" ? null : DecodeHexText(cols[10]);
            string? branch = cols[11] == "null" ? null : DecodeHexText(cols[12]);
            string? started = cols[13] == "null" ? null : DecodeHexText(cols[14]);
            string? ended = cols[15] == "null" ? null : DecodeHexText(cols[16]);
            string? summaryJson = cols[17] == "null" ? null : DecodeHexText(cols[18]);
            int? problemsCount = cols[19] == "null" ? null : IntOrNull(cols[20]);
            string? contextLimitSource = cols[21] == "null" ? null : DecodeHexText(cols[22]);
            string payloadText = DecodeHexText(cols[23]);

            List<JsonElement>? sessionTurns = null;
            if (turnsBySession is not null)
                turnsBySession.TryGetValue(sessionId, out sessionTurns);

            JsonElement payloadElement;
            if (hasTurnTable && JsonNode.Parse(payloadText) is JsonObject obj && !obj.ContainsKey("turns"))
            {
                JsonArray turnsArray = [];
                if (sessionTurns is not null)
                {
                    foreach (JsonElement turn in sessionTurns)
                        turnsArray.Add(JsonNode.Parse(turn.GetRawText()));
                }

                obj["turns"] = turnsArray;
                payloadElement = JsonSerializer.Deserialize<JsonElement>(obj.ToJsonString());
            }
            else
            {
                payloadElement = JsonSerializer.Deserialize<JsonElement>(payloadText);
                if (sessionTurns is null
                    && payloadElement.ValueKind == JsonValueKind.Object
                    && payloadElement.TryGetProperty("turns", out JsonElement embeddedTurns)
                    && embeddedTurns.ValueKind == JsonValueKind.Array)
                {
                    sessionTurns = [];
                    foreach (JsonElement item in embeddedTurns.EnumerateArray())
                        sessionTurns.Add(item.Clone());
                }
            }

            rows.Add(new SessionSummaryRow(
                SessionId: sessionId,
                Harness: harness,
                Label: label,
                IsSubagent: isSubagent,
                ParentSession: parentSession,
                AgentName: agentName,
                Repo: repo,
                Branch: branch,
                Started: started,
                Ended: ended,
                SummaryJson: summaryJson,
                ProblemsCount: problemsCount,
                ContextLimitSource: contextLimitSource,
                Payload: payloadElement,
                Turns: sessionTurns));
        }

        return rows;
    }

    private Dictionary<string, List<JsonElement>> ReadSessionTurnsMap(string? singleSessionId)
    {
        string whereClause = singleSessionId is null ? string.Empty : $"WHERE session_id = {TextExpr(singleSessionId)} ";
        string output = ExecuteSqlite(
            ".mode tabs\n" +
            ".headers off\n" +
            "SELECT " +
            "hex(CAST(session_id AS BLOB)), " +
            "typeof(turn_json), " +
            "hex(CASE WHEN typeof(turn_json) = 'blob' THEN turn_json ELSE CAST(turn_json AS BLOB) END) " +
            $"FROM session_turn {whereClause}ORDER BY session_id, turn_index;");

        Dictionary<string, List<JsonElement>> map = new(StringComparer.Ordinal);
        foreach (string line in TabLines(output))
        {
            string[] cols = line.Split('\t', StringSplitOptions.None);
            if (cols.Length < 3)
                continue;

            string sessionId = DecodeHexText(cols[0]);
            if (!TryDecodeStoredColumn(cols[1], cols[2], out string turnJson))
                continue;

            try
            {
                JsonElement turnElement = JsonSerializer.Deserialize<JsonElement>(turnJson);
                if (turnElement.ValueKind != JsonValueKind.Object)
                    continue;

                if (!map.TryGetValue(sessionId, out List<JsonElement>? list))
                {
                    list = [];
                    map[sessionId] = list;
                }

                list.Add(turnElement);
            }
            catch (JsonException)
            {
                // A malformed stored turn fragment is skipped rather than failing the whole read.
            }
        }

        return map;
    }

    private bool HasSessionTurnTable()
    {
        string output = ExecuteSqlite(
            ".mode tabs\n" +
            ".headers off\n" +
            "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = 'session_turn' LIMIT 1;");
        return Lines(output).Any();
    }

    private static SplitSessionPayload SplitSession(SessionSummaryRow row)
    {
        List<(int TurnIndex, string TurnJson)> turns = [];
        if (row.Payload.ValueKind != JsonValueKind.Object)
        {
            string rawPayload = row.Payload.ValueKind == JsonValueKind.Undefined ? "{}" : row.Payload.GetRawText();
            if (row.Turns is not null)
            {
                for (int i = 0; i < row.Turns.Count; i++)
                    turns.Add((TurnIndexOf(row.Turns[i], i), row.Turns[i].GetRawText()));
            }

            return new SplitSessionPayload(rawPayload, row.SummaryJson, row.ProblemsCount, row.ContextLimitSource, turns);
        }

        JsonObject obj = (JsonObject)JsonNode.Parse(row.Payload.GetRawText())!;

        string? summaryJson = row.SummaryJson;
        if (summaryJson is null && obj.TryGetPropertyValue("summary", out JsonNode? summaryNode) && summaryNode is not null)
            summaryJson = summaryNode.ToJsonString();

        int? problemsCount = row.ProblemsCount;
        if (problemsCount is null && obj.TryGetPropertyValue("problems", out JsonNode? problemsNode) && problemsNode is JsonArray problemsArray)
            problemsCount = problemsArray.Count;

        string? contextLimitSource = row.ContextLimitSource;
        if (contextLimitSource is null
            && obj.TryGetPropertyValue("context", out JsonNode? contextNode)
            && contextNode is JsonObject contextObj
            && contextObj.TryGetPropertyValue("contextLimitSource", out JsonNode? clsNode)
            && clsNode is JsonValue clsVal
            && clsVal.TryGetValue(out string? clsString)
            && !string.IsNullOrEmpty(clsString))
        {
            contextLimitSource = clsString;
        }

        if (obj.TryGetPropertyValue("turns", out JsonNode? turnsNode))
        {
            obj.Remove("turns");
            if (turnsNode is JsonArray turnsArray)
            {
                for (int i = 0; i < turnsArray.Count; i++)
                {
                    JsonNode? item = turnsArray[i];
                    if (item is null)
                        continue;

                    int idx = i;
                    if (item is JsonObject turnObj
                        && turnObj.TryGetPropertyValue("index", out JsonNode? idxNode)
                        && idxNode is JsonValue idxVal
                        && idxVal.TryGetValue(out int explicitIdx))
                    {
                        idx = explicitIdx;
                    }

                    turns.Add((idx, item.ToJsonString()));
                }
            }
        }
        else if (row.Turns is not null)
        {
            for (int i = 0; i < row.Turns.Count; i++)
                turns.Add((TurnIndexOf(row.Turns[i], i), row.Turns[i].GetRawText()));
        }

        return new SplitSessionPayload(obj.ToJsonString(), summaryJson, problemsCount, contextLimitSource, turns);
    }

    private static int TurnIndexOf(JsonElement turn, int position)
    {
        if (turn.ValueKind == JsonValueKind.Object
            && turn.TryGetProperty("index", out JsonElement idxEl)
            && idxEl.ValueKind == JsonValueKind.Number
            && idxEl.TryGetInt32(out int idx))
        {
            return idx;
        }

        return position;
    }

    private IReadOnlyList<Finding> QueryFindings(string suffixSql)
    {
        string output = ExecuteSqlite(
            ".mode tabs\n" +
            ".headers off\n" +
            "SELECT " +
            "hex(CAST(id AS BLOB)), " +
            "hex(CAST(detector_id AS BLOB)), " +
            "hex(CAST(title AS BLOB)), " +
            "hex(CAST(mechanism AS BLOB)), " +
            "hex(CAST(confidence AS BLOB)), " +
            "typeof(estimated_waste_tokens), COALESCE(estimated_waste_tokens, 0), " +
            "hex(CAST(recommendation AS BLOB)), " +
            "hex(CAST(error_bar_json AS BLOB)), " +
            "hex(CAST(evidence_links_json AS BLOB)), " +
            "hex(CAST(outcome_risk_caveat AS BLOB)), " +
            "typeof(run_id), COALESCE(hex(CAST(run_id AS BLOB)), ''), " +
            "typeof(session_id), COALESCE(hex(CAST(session_id AS BLOB)), ''), " +
            "rank_score, " +
            "typeof(payload), COALESCE(hex(CAST(payload AS BLOB)), '') " +
            $"FROM finding {suffixSql};");

        List<Finding> findings = [];
        foreach (string line in TabLines(output))
        {
            string[] cols = line.Split('\t', StringSplitOptions.None);
            if (cols.Length < 18)
                continue;

            findings.Add(ParseFindingRow(cols));
        }

        return findings;
    }

    private static Finding ParseFindingRow(string[] cols)
    {
        string id = DecodeHexText(cols[0]);
        string detectorId = DecodeHexText(cols[1]);
        string title = DecodeHexText(cols[2]);
        string mechanism = DecodeHexText(cols[3]);
        string confidence = DecodeHexText(cols[4]);
        long? estimatedWasteTokens = cols[5] == "null"
            ? null
            : LongOrNull(cols[6]);
        string recommendation = DecodeHexText(cols[7]);
        string errorBarRaw = DecodeHexText(cols[8]);
        FindingErrorBar? errorBar = errorBarRaw == "null"
            ? null
            : JsonSerializer.Deserialize<FindingErrorBar>(errorBarRaw, CamelCaseJsonOptions);
        IReadOnlyList<FindingEvidenceLink> evidenceLinks =
            JsonSerializer.Deserialize<List<FindingEvidenceLink>>(DecodeHexText(cols[9]), CamelCaseJsonOptions)
            ?? [];
        string outcomeRiskCaveat = DecodeHexText(cols[10]);
        string? runId = cols[11] == "null" ? null : DecodeHexText(cols[12]);
        string? sessionId = cols[13] == "null" ? null : DecodeHexText(cols[14]);
        double rankScore = DoubleOrZero(cols[15]);

        Dictionary<string, object?>? payloadDict = null;
        string? measurementClass = null;
        string? confidenceBasis = null;
        string? whatWouldRaiseIt = null;
        if (cols[16] != "null")
        {
            ExtractFindingPayloadFields(
                DecodeHexText(cols[17]),
                out payloadDict,
                out measurementClass,
                out confidenceBasis,
                out whatWouldRaiseIt);
        }

        return new Finding(
            Id: id,
            DetectorId: detectorId,
            Title: title,
            Mechanism: mechanism,
            EvidenceLinks: evidenceLinks,
            Confidence: confidence,
            EstimatedWasteTokens: estimatedWasteTokens,
            Recommendation: recommendation,
            ErrorBar: errorBar,
            OutcomeRiskCaveat: outcomeRiskCaveat,
            RunId: runId,
            SessionId: sessionId,
            RankScore: rankScore,
            MeasurementClass: measurementClass,
            ConfidenceBasis: confidenceBasis,
            WhatWouldRaiseIt: whatWouldRaiseIt,
            Payload: payloadDict);
    }

    private static string? BuildFindingPayloadJson(Finding finding)
    {
        if (finding.Payload is not null)
        {
            return finding.Payload.Count == 0
                ? null
                : JsonSerializer.Serialize(finding.Payload, CamelCaseJsonOptions);
        }

        Dictionary<string, object?> extra = new(StringComparer.Ordinal);
        if (!string.IsNullOrEmpty(finding.MeasurementClass))
            extra["measurementClass"] = finding.MeasurementClass;
        if (!string.IsNullOrEmpty(finding.ConfidenceBasis))
            extra["confidenceBasis"] = finding.ConfidenceBasis;
        if (!string.IsNullOrEmpty(finding.WhatWouldRaiseIt))
            extra["whatWouldRaiseIt"] = finding.WhatWouldRaiseIt;

        return extra.Count == 0 ? null : JsonSerializer.Serialize(extra, CamelCaseJsonOptions);
    }

    private static void ExtractFindingPayloadFields(
        string payloadJson,
        out Dictionary<string, object?>? payloadDict,
        out string? measurementClass,
        out string? confidenceBasis,
        out string? whatWouldRaiseIt)
    {
        payloadDict = null;
        measurementClass = null;
        confidenceBasis = null;
        whatWouldRaiseIt = null;

        try
        {
            using JsonDocument doc = JsonDocument.Parse(payloadJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return;

            payloadDict = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (JsonProperty prop in doc.RootElement.EnumerateObject())
            {
                payloadDict[prop.Name] = prop.Value.Clone();
                if (prop.Value.ValueKind != JsonValueKind.String)
                    continue;

                switch (prop.Name)
                {
                    case "measurementClass":
                        measurementClass = prop.Value.GetString();
                        break;
                    case "confidenceBasis":
                        confidenceBasis = prop.Value.GetString();
                        break;
                    case "whatWouldRaiseIt":
                        whatWouldRaiseIt = prop.Value.GetString();
                        break;
                }
            }
        }
        catch (JsonException)
        {
            // A malformed extra payload keeps every field except the payload itself.
        }
    }

    private IReadOnlyList<RunRow> QueryRuns(string suffixSql)
    {
        string output = ExecuteSqlite(
            ".mode tabs\n" +
            ".headers off\n" +
            "SELECT " +
            "hex(CAST(run_id AS BLOB)), " +
            "hex(CAST(harness AS BLOB)), " +
            "typeof(label), COALESCE(hex(CAST(label AS BLOB)), ''), " +
            "hex(CAST(grouping_basis AS BLOB)), " +
            "typeof(grouping_rule), COALESCE(hex(CAST(grouping_rule AS BLOB)), ''), " +
            "typeof(working_directory), COALESCE(hex(CAST(working_directory AS BLOB)), ''), " +
            "typeof(started), COALESCE(hex(CAST(started AS BLOB)), ''), " +
            "typeof(ended), COALESCE(hex(CAST(ended AS BLOB)), ''), " +
            "execution_count, " +
            "typeof(payload), COALESCE(hex(CAST(payload AS BLOB)), '') " +
            $"FROM run {suffixSql};");

        List<RunRow> rows = [];
        foreach (string line in TabLines(output))
        {
            string[] cols = line.Split('\t', StringSplitOptions.None);
            if (cols.Length < 16)
                continue;

            JsonElement? payload = cols[14] == "null" ? null : ParseJsonOrNull(DecodeHexText(cols[15]));
            rows.Add(new RunRow(
                RunId: DecodeHexText(cols[0]),
                Harness: DecodeHexText(cols[1]),
                Label: cols[2] == "null" ? null : DecodeHexText(cols[3]),
                GroupingBasis: DecodeHexText(cols[4]),
                GroupingRule: cols[5] == "null" ? null : DecodeHexText(cols[6]),
                WorkingDirectory: cols[7] == "null" ? null : DecodeHexText(cols[8]),
                Started: cols[9] == "null" ? null : DecodeHexText(cols[10]),
                Ended: cols[11] == "null" ? null : DecodeHexText(cols[12]),
                ExecutionCount: IntOrZero(cols[13]),
                Payload: payload));
        }

        return rows;
    }

    private IReadOnlyList<ExecutionRow> QueryExecutions(string suffixSql)
    {
        string output = ExecuteSqlite(
            ".mode tabs\n" +
            ".headers off\n" +
            "SELECT " +
            "hex(CAST(execution_id AS BLOB)), " +
            "hex(CAST(run_id AS BLOB)), " +
            "typeof(session_id), COALESCE(hex(CAST(session_id AS BLOB)), ''), " +
            "typeof(parent_execution_id), COALESCE(hex(CAST(parent_execution_id AS BLOB)), ''), " +
            "hex(CAST(harness AS BLOB)), " +
            "typeof(agent_name), COALESCE(hex(CAST(agent_name AS BLOB)), ''), " +
            "is_root, " +
            "typeof(started), COALESCE(hex(CAST(started AS BLOB)), ''), " +
            "typeof(ended), COALESCE(hex(CAST(ended AS BLOB)), ''), " +
            "hex(CAST(parent_linkage_json AS BLOB)), " +
            "typeof(payload), COALESCE(hex(CAST(payload AS BLOB)), '') " +
            $"FROM execution {suffixSql};");

        List<ExecutionRow> rows = [];
        foreach (string line in TabLines(output))
        {
            string[] cols = line.Split('\t', StringSplitOptions.None);
            if (cols.Length < 17)
                continue;

            JsonElement linkage = ParseJsonOrNull(DecodeHexText(cols[14]))
                ?? JsonSerializer.SerializeToElement(new { status = "not_measurable" });
            JsonElement? payload = cols[15] == "null" ? null : ParseJsonOrNull(DecodeHexText(cols[16]));
            rows.Add(new ExecutionRow(
                ExecutionId: DecodeHexText(cols[0]),
                RunId: DecodeHexText(cols[1]),
                SessionId: cols[2] == "null" ? null : DecodeHexText(cols[3]),
                ParentExecutionId: cols[4] == "null" ? null : DecodeHexText(cols[5]),
                Harness: DecodeHexText(cols[6]),
                AgentName: cols[7] == "null" ? null : DecodeHexText(cols[8]),
                IsRoot: cols[9] != "0",
                Started: cols[10] == "null" ? null : DecodeHexText(cols[11]),
                Ended: cols[12] == "null" ? null : DecodeHexText(cols[13]),
                ParentLinkage: linkage,
                Payload: payload));
        }

        return rows;
    }

    private IReadOnlyList<HarnessRollupRow> QueryHarnessRollups(string suffixSql)
    {
        string output = ExecuteSqlite(
            ".mode tabs\n" +
            ".headers off\n" +
            "SELECT " +
            "hex(CAST(harness AS BLOB)), " +
            "sample_count, " +
            "typeof(context_pressure_median), COALESCE(context_pressure_median, 0.0), " +
            "typeof(context_pressure_p95), COALESCE(context_pressure_p95, 0.0), " +
            "typeof(cache_hit_rate), COALESCE(cache_hit_rate, 0.0), " +
            "typeof(tool_yield), COALESCE(tool_yield, 0.0), " +
            "typeof(delegation_overhead), COALESCE(delegation_overhead, 0.0), " +
            "typeof(field_coverage), COALESCE(field_coverage, 0.0), " +
            "hex(CAST(measurability_json AS BLOB)), " +
            "typeof(payload), COALESCE(hex(CAST(payload AS BLOB)), '') " +
            $"FROM harness_rollup {suffixSql};");

        List<HarnessRollupRow> rows = [];
        foreach (string line in TabLines(output))
        {
            string[] cols = line.Split('\t', StringSplitOptions.None);
            if (cols.Length < 17)
                continue;

            Dictionary<string, JsonElement> measurability =
                JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(DecodeHexText(cols[14]), RecordJsonOptions)
                ?? new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            JsonElement? payload = cols[15] == "null" ? null : ParseJsonOrNull(DecodeHexText(cols[16]));

            rows.Add(new HarnessRollupRow(
                Harness: DecodeHexText(cols[0]),
                SampleCount: IntOrZero(cols[1]),
                ContextPressureMedian: ParseNullableDouble(cols[2], cols[3]),
                ContextPressureP95: ParseNullableDouble(cols[4], cols[5]),
                CacheHitRate: ParseNullableDouble(cols[6], cols[7]),
                ToolYield: ParseNullableDouble(cols[8], cols[9]),
                DelegationOverhead: ParseNullableDouble(cols[10], cols[11]),
                FieldCoverage: ParseNullableDouble(cols[12], cols[13]),
                Measurability: measurability,
                Payload: payload));
        }

        return rows;
    }

    private IReadOnlyList<StoredProblemRow> QueryStoredProblems(string suffixSql)
    {
        string output = ExecuteSqlite(
            ".mode tabs\n" +
            ".headers off\n" +
            "SELECT " +
            "id, " +
            "COALESCE(hex(CAST(span_id AS BLOB)), '2d'), " +
            "hex(CAST(severity AS BLOB)), " +
            "hex(CAST(code AS BLOB)), " +
            "hex(CAST(message AS BLOB)), " +
            "typeof(location), COALESCE(hex(CAST(location AS BLOB)), ''), " +
            "typeof(session_id), COALESCE(hex(CAST(session_id AS BLOB)), ''), " +
            "typeof(harness), COALESCE(hex(CAST(harness AS BLOB)), ''), " +
            "typeof(timestamp), COALESCE(hex(CAST(timestamp AS BLOB)), ''), " +
            "typeof(stack), COALESCE(hex(CAST(stack AS BLOB)), '') " +
            $"FROM problems {suffixSql};");

        List<StoredProblemRow> rows = [];
        foreach (string line in TabLines(output))
        {
            string[] cols = line.Split('\t', StringSplitOptions.None);
            if (cols.Length < 15)
                continue;

            if (!int.TryParse(cols[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int problemId))
                continue;

            rows.Add(new StoredProblemRow(
                Id: problemId,
                SpanId: DecodeHexOrEmpty(cols[1]),
                Severity: DecodeHexText(cols[2]),
                Code: DecodeHexText(cols[3]),
                Message: DecodeHexText(cols[4]),
                Location: cols[5] == "null" ? null : DecodeHexText(cols[6]),
                SessionId: cols[7] == "null" ? null : DecodeHexText(cols[8]),
                Harness: cols[9] == "null" ? null : DecodeHexText(cols[10]),
                Timestamp: cols[11] == "null" ? null : DecodeHexText(cols[12]),
                Stack: cols[13] == "null" ? null : DecodeHexText(cols[14])));
        }

        return rows;
    }

    private IReadOnlyList<QuarantineRow> QueryQuarantine(string suffixSql)
    {
        string output = ExecuteSqlite(
            ".mode tabs\n" +
            ".headers off\n" +
            "SELECT " +
            "COALESCE(hex(CAST(span_id AS BLOB)), '2d'), " +
            "hex(CAST(namespaces AS BLOB)), " +
            "hex(CAST(reason AS BLOB)), " +
            "typeof(source), COALESCE(hex(CAST(source AS BLOB)), ''), " +
            "typeof(name), COALESCE(hex(CAST(name AS BLOB)), ''), " +
            "typeof(timestamp), COALESCE(hex(CAST(timestamp AS BLOB)), ''), " +
            "typeof(raw_payload), COALESCE(hex(CAST(raw_payload AS BLOB)), ''), " +
            "typeof(harness), COALESCE(hex(CAST(harness AS BLOB)), '') " +
            $"FROM quarantine {suffixSql};");

        List<QuarantineRow> rows = [];
        foreach (string line in TabLines(output))
        {
            string[] cols = line.Split('\t', StringSplitOptions.None);
            if (cols.Length < 13)
                continue;

            List<string> namespaces = JsonSerializer.Deserialize<List<string>>(DecodeHexText(cols[1]), RecordJsonOptions) ?? [];
            rows.Add(new QuarantineRow(
                SpanId: DecodeHexText(cols[0]),
                ObservedNamespaces: namespaces,
                Reason: DecodeHexText(cols[2]),
                Source: cols[3] == "null" ? null : DecodeHexText(cols[4]),
                Name: cols[5] == "null" ? null : DecodeHexText(cols[6]),
                Timestamp: cols[7] == "null" ? null : DecodeHexText(cols[8]),
                RawPayload: cols[9] == "null" ? null : DecodeHexText(cols[10]),
                Harness: cols[11] == "null" ? null : DecodeHexText(cols[12])));
        }

        return rows;
    }

    private IReadOnlyList<SourceCheckpoint> QuerySourceCheckpoints(string suffixSql)
    {
        string output = ExecuteSqlite(
            ".mode tabs\n" +
            ".headers off\n" +
            "SELECT " +
            "hex(CAST(harness_id AS BLOB)), " +
            "hex(CAST(source_key AS BLOB)), " +
            "hex(CAST(provider_id AS BLOB)), " +
            "hex(CAST(parser_id AS BLOB)), " +
            "hex(CAST(parser_contract_version AS BLOB)), " +
            "hex(CAST(format AS BLOB)), " +
            "hex(CAST(source_root_label AS BLOB)), " +
            "hex(CAST(revision_token AS BLOB)), " +
            "hex(CAST(covered_from_utc AS BLOB)), " +
            "hex(CAST(covered_through_utc AS BLOB)), " +
            "hex(CAST(last_attempt_utc AS BLOB)), " +
            "typeof(last_success_utc), COALESCE(hex(CAST(last_success_utc AS BLOB)), ''), " +
            "hex(CAST(last_status AS BLOB)), " +
            "typeof(last_error_code), COALESCE(hex(CAST(last_error_code AS BLOB)), ''), " +
            "unit_count, " +
            "record_count " +
            $"FROM source_checkpoint {suffixSql};");

        List<SourceCheckpoint> rows = [];
        foreach (string line in TabLines(output))
        {
            string[] cols = line.Split('\t', StringSplitOptions.None);
            if (cols.Length < 18)
                continue;

            rows.Add(new SourceCheckpoint(
                HarnessId: DecodeHexText(cols[0]),
                SourceKey: DecodeHexText(cols[1]),
                ProviderId: DecodeHexText(cols[2]),
                ParserId: DecodeHexText(cols[3]),
                ParserContractVersion: DecodeHexText(cols[4]),
                Format: DecodeHexText(cols[5]),
                SourceRootLabel: DecodeHexText(cols[6]),
                RevisionToken: DecodeHexText(cols[7]),
                CoveredFromUtc: DecodeHexText(cols[8]),
                CoveredThroughUtc: DecodeHexText(cols[9]),
                LastAttemptUtc: DecodeHexText(cols[10]),
                LastSuccessUtc: cols[11] == "null" ? null : DecodeHexText(cols[12]),
                LastStatus: DecodeHexText(cols[13]),
                LastErrorCode: cols[14] == "null" ? null : DecodeHexText(cols[15]),
                UnitCount: IntOrZero(cols[16]),
                RecordCount: IntOrZero(cols[17])));
        }

        return rows;
    }

    private IReadOnlyList<RefreshRunRow> QueryRefreshRuns(string suffixSql)
    {
        string output = ExecuteSqlite(
            ".mode tabs\n" +
            ".headers off\n" +
            "SELECT " +
            "hex(CAST(id AS BLOB)), " +
            "hex(CAST(started_at AS BLOB)), " +
            "typeof(completed_at), COALESCE(hex(CAST(completed_at AS BLOB)), ''), " +
            "hex(CAST(status AS BLOB)), " +
            "pid, " +
            "hex(CAST(trigger AS BLOB)), " +
            "typeof(summary), COALESCE(hex(CAST(summary AS BLOB)), ''), " +
            "typeof(history_weeks), COALESCE(history_weeks, 0) " +
            $"FROM refresh_run {suffixSql};");

        List<RefreshRunRow> rows = [];
        foreach (string line in TabLines(output))
        {
            string[] cols = line.Split('\t', StringSplitOptions.None);
            if (cols.Length < 11)
                continue;

            int? weeks = cols[9] == "null"
                ? null
                : CanonSchema.NormalizeHistoryWeeks(LongOrNull(cols[10]) ?? 0);

            rows.Add(new RefreshRunRow(
                Id: DecodeHexText(cols[0]),
                StartedAt: DecodeHexText(cols[1]),
                CompletedAt: cols[2] == "null" ? null : DecodeHexText(cols[3]),
                Status: DecodeHexText(cols[4]),
                Pid: IntOrZero(cols[5]),
                Trigger: DecodeHexText(cols[6]),
                Summary: cols[7] == "null" ? null : DecodeHexText(cols[8]),
                HistoryWeeks: weeks));
        }

        return rows;
    }

    private IReadOnlyList<OtlpLog> QueryLogs(string table, bool ejected, int limit)
    {
        string limitSql = limit.ToString(CultureInfo.InvariantCulture);
        StringBuilder script = new();
        script.AppendLine(".mode tabs");
        script.AppendLine(".headers off");
        if (ejected)
            script.AppendLine("BEGIN IMMEDIATE;");

        AppendLineInvariant(script, $"SELECT COALESCE(hex(CAST(log_id AS BLOB)), '2d'), COALESCE(hex(CAST(payload AS BLOB)), '2d') FROM {table} ORDER BY rowid LIMIT {limitSql};");
        if (ejected)
        {
            AppendLineInvariant(script, $"DELETE FROM {table} WHERE log_id IN (SELECT log_id FROM {table} ORDER BY rowid LIMIT {limitSql});");
            script.AppendLine("COMMIT;");
        }

        string output = ExecuteSqlite(script.ToString());
        List<OtlpLog> logs = [];
        foreach (string line in TabLines(output))
        {
            string[] cols = line.Split('\t', StringSplitOptions.None);
            if (cols.Length < 2)
                continue;

            string logId = DecodeHexOrEmpty(cols[0]);
            string payloadJson = DecodeHexOrEmpty(cols[1]);
            if (string.IsNullOrWhiteSpace(payloadJson))
                continue;

            try
            {
                OtlpLog? log = JsonSerializer.Deserialize<OtlpLog>(payloadJson, RecordJsonOptions);
                if (log is not null)
                    logs.Add(log with { LogId = logId });
            }
            catch (JsonException)
            {
                // A malformed stored log payload is dropped rather than failing the whole batch.
            }
        }

        return logs;
    }

    private static void AppendMetricPointSqls(StringBuilder script, OtlpMetric metric, string? source)
    {
        string resourceJson = JsonSerializer.Serialize(metric.Resource, RecordJsonOptions);
        string scopeJson = JsonSerializer.Serialize(metric.Scope, RecordJsonOptions);
        string temporality = metric.AggregationTemporality.HasValue
            ? metric.AggregationTemporality.Value.ToString(CultureInfo.InvariantCulture)
            : "NULL";

        if (metric.DataPoints.Count == 0)
        {
            script.AppendLine(AppendMetricPointSql(metric, point: null, pointKey: metric.Name, resourceJson, scopeJson, temporality, source));
            return;
        }

        foreach (OtlpMetricDataPoint point in metric.DataPoints)
        {
            string pointAttributesJson = JsonSerializer.Serialize(point.Attributes, RecordJsonOptions);
            string pointKey = $"{metric.Name}|{point.TimeUnixNano ?? string.Empty}|{pointAttributesJson}";
            script.AppendLine(AppendMetricPointSql(metric, point, pointKey, resourceJson, scopeJson, temporality, source));
        }
    }

    private static string AppendMetricPointSql(
        OtlpMetric metric,
        OtlpMetricDataPoint? point,
        string pointKey,
        string resourceJson,
        string scopeJson,
        string temporality,
        string? source)
    {
        string pointAttributesJson = point is null
            ? JsonSerializer.Serialize(new Dictionary<string, JsonElement>(StringComparer.Ordinal), RecordJsonOptions)
            : JsonSerializer.Serialize(point.Attributes, RecordJsonOptions);

        return
            "INSERT OR REPLACE INTO metric_point (point_key, name, kind, unit, aggregation_temporality, time_unix_nano, start_time_unix_nano, " +
            "timestamp, value, point_count, point_sum, attributes_json, resource_json, scope_json, source) VALUES (" +
            $"{TextExpr(pointKey)}, {TextExpr(metric.Name)}, {TextExpr(metric.Kind)}, {NullableTextExpr(metric.Unit)}, {temporality}, " +
            $"{NullableTextExpr(point?.TimeUnixNano)}, {NullableTextExpr(point?.StartTimeUnixNano)}, {NullableTextExpr(point?.TimeUnixNano)}, " +
            $"{NullableDoubleSql(point?.Value)}, {NullableDoubleSql(point?.Count)}, {NullableDoubleSql(point?.Sum)}, " +
            $"{TextExpr(pointAttributesJson)}, {TextExpr(resourceJson)}, {TextExpr(scopeJson)}, {NullableTextExpr(source)});";
    }

    private void AppendDirtySessionUpdate(StringBuilder script, HashSet<string>? dirtyKeys)
    {
        long prevSeq = RecordsWriteMarker();
        long nextSeq = prevSeq + 1;
        string? existingJson = GetMetadata(CanonSchema.ProjectionDirtySessionsKey);

        long fromSeq = prevSeq;
        bool wildcard = dirtyKeys is null;
        HashSet<string>? mergedKeys = dirtyKeys is null ? null : new HashSet<string>(dirtyKeys, StringComparer.Ordinal);

        if (existingJson is not null)
        {
            try
            {
                using JsonDocument doc = JsonDocument.Parse(existingJson);
                JsonElement root = doc.RootElement;
                if (root.ValueKind == JsonValueKind.Object
                    && root.TryGetProperty("fromSeq", out JsonElement fromEl)
                    && root.TryGetProperty("toSeq", out JsonElement toEl)
                    && root.TryGetProperty("keys", out JsonElement keysEl)
                    && fromEl.TryGetInt64(out long existingFrom)
                    && toEl.TryGetInt64(out long existingTo)
                    && existingTo == prevSeq)
                {
                    fromSeq = existingFrom;
                    if (keysEl.ValueKind == JsonValueKind.String && string.Equals(keysEl.GetString(), "*", StringComparison.Ordinal))
                    {
                        wildcard = true;
                        mergedKeys = null;
                    }
                    else if (!wildcard && keysEl.ValueKind == JsonValueKind.Array && mergedKeys is not null)
                    {
                        foreach (JsonElement item in keysEl.EnumerateArray())
                        {
                            if (item.ValueKind != JsonValueKind.String)
                                continue;

                            string? itemValue = item.GetString();
                            if (itemValue is not null)
                                mergedKeys.Add(itemValue);
                        }
                    }
                }
            }
            catch (JsonException)
            {
                // Corrupt prior metadata restarts dirty tracking from prevSeq.
            }
        }

        object payload = wildcard
            ? new { fromSeq, toSeq = nextSeq, keys = "*" }
            : new { fromSeq, toSeq = nextSeq, keys = mergedKeys!.Order(StringComparer.Ordinal).ToArray() };
        string serialized = JsonSerializer.Serialize(payload);
        AppendLineInvariant(script, $"INSERT OR REPLACE INTO metadata (key, value) VALUES ({TextExpr(CanonSchema.ProjectionDirtySessionsKey)}, {TextExpr(serialized)});");
    }

    private static void AppendBumpRecordsWriteMarker(StringBuilder script)
    {
        string keyExpr = TextExpr(CanonSchema.RecordsWriteMarkerKey);
        AppendLineInvariant(script, $"INSERT INTO metadata (key, value) VALUES ({keyExpr}, '1') ON CONFLICT(key) DO UPDATE SET value = CAST((COALESCE(CAST(metadata.value AS INTEGER), 0) + 1) AS TEXT);");
    }

    private static string NullableDoubleSql(double? value) =>
        value.HasValue && double.IsFinite(value.Value)
            ? value.Value.ToString("G17", CultureInfo.InvariantCulture)
            : "NULL";

    /// <summary>
    /// Renders a non-nullable REAL column. A non-finite value has no SQL literal and would make the
    /// whole batch throw, so it is written as 0.
    /// </summary>
    private static string RequiredDoubleSql(double value) =>
        (double.IsFinite(value) ? value : 0d).ToString("G17", CultureInfo.InvariantCulture);

    private static double? ParseNullableDouble(string typeCol, string valCol)
    {
        if (string.Equals(typeCol, "null", StringComparison.Ordinal))
            return null;

        return double.TryParse(valCol, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed) && double.IsFinite(parsed)
            ? parsed
            : null;
    }

    /// <summary>Decodes a hex-encoded text column, where the ASCII hyphen (<c>2d</c>) marks an empty value.</summary>
    private static string DecodeHexOrEmpty(string hex) =>
        string.Equals(hex, "2d", StringComparison.Ordinal) ? string.Empty : DecodeHexText(hex);

    private static JsonElement? ParseJsonOrNull(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;

        try
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            return doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string BuildProblemKey(string spanId, string code, string? location)
    {
        object?[] parts = ["span-problem", spanId, code, location];
        return JsonSerializer.Serialize(parts);
    }

    private record SplitSessionPayload(
        string PayloadJson,
        string? SummaryJson,
        int? ProblemsCount,
        string? ContextLimitSource,
        IReadOnlyList<(int TurnIndex, string TurnJson)> Turns);
}
