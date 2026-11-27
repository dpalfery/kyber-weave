using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using KyberDash.Contracts.Repositories;
using KyberDash.Core.Exceptions;
using KyberDash.Core.Repositories;
using KyberDash.Core.Resilience;
using KyberDash.Data.Collector;
using KyberDash.Domain;
using KyberDash.Domain.Schema;
using Microsoft.Data.Sqlite;

namespace KyberDash.Data.Repositories;

/// <summary>
/// The read half of <see cref="SqliteRelationalStore"/>: schema reads, session projections, and
/// content-free figures over a SQLite relational store.
/// </summary>
/// <summary>
/// Low-level read helpers shared by the relational store's reader: row mapping, parsing, and the
/// parameterized read seam.
/// </summary>
public abstract class SqliteRelationalStoreReadBase : BaseSqliteRepository
{
    private protected const int BusyTimeoutMilliseconds = 5000;
    private protected const int BusyAttempts = 3;

    private protected IRelationalStoreFileSystem fileSystem { get; }

    /// <summary>Initializes the read half over <paramref name="databasePath"/>.</summary>
    /// <param name="databasePath">Path to the SQLite database file.</param>
    /// <param name="circuitBreaker">Circuit breaker guarding every <c>sqlite3</c> invocation.</param>
    /// <param name="fileSystem">Filesystem seam; defaults to <see cref="PhysicalRelationalStoreFileSystem.Instance"/> when <see langword="null"/>.</param>
    protected SqliteRelationalStoreReadBase(
        string databasePath,
        ICircuitBreaker circuitBreaker,
        IRelationalStoreFileSystem? fileSystem)
        : base(
            databasePath,
            circuitBreaker,
            batchTimeout: TimeSpan.FromSeconds(120),
            busyTimeoutMilliseconds: BusyTimeoutMilliseconds,
            busyAttempts: BusyAttempts)
    {
        this.fileSystem = fileSystem ?? PhysicalRelationalStoreFileSystem.Instance;
    }

    /// <summary>Ensures the parent directory of <see cref="BaseSqliteRepository.DatabasePath"/> exists via the filesystem seam.</summary>
    private protected void EnsureDatabaseDirectory()
    {
        string? directory = Path.GetDirectoryName(DatabasePath);
        if (!string.IsNullOrEmpty(directory))
        {
            fileSystem.CreateDirectory(directory);
        }
    }

    /// <summary>
    /// Ensures the parent directory exists before a statement or query opens the database file, so
    /// a read or write on a store whose file does not yet exist never fails on a missing directory.
    /// </summary>
    private protected void EnsureDatabaseDirectoryIfMissing()
    {
        if (!fileSystem.FileExists(DatabasePath))
        {
            EnsureDatabaseDirectory();
        }
    }

    /// <summary>Formats a timestamp as the round-trip ISO-8601 text the store writes.</summary>
    private protected static string FormatTimestamp(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    /// <summary>Formats an integer as invariant SQL text.</summary>
    private protected static string IntSql(int value) => value.ToString(CultureInfo.InvariantCulture);

    private protected static List<(int Position, CollectedRecord Record)> ResolveSessionTurnEntriesForReader(
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

    private protected static ReadTimeTurnSummary BuildReaderTurnSummary(
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

    private protected static long? TryFindReaderTokenCount(IReadOnlyList<CollectedTokenCount> tokens, TokenClass tokenClass)
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

    private protected static long? ComputeReaderDurationMs(CollectedRecord record)
    {
        if (record.EndedAt.HasValue)
        {
            return Math.Max(0L, (long)Math.Round((record.EndedAt.Value - record.StartedAt).TotalMilliseconds));
        }

        return record.ReportedDurationMs;
    }

    private protected static ReadTimeCostSummary ToReaderCostSummary(CostBlock block)
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

    private protected static ReadTimeCostSummary AggregateReaderCostBlocks(
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

    private protected static List<ReadTimeSessionToolSummary> BuildReaderToolSummaries(
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

    private protected static List<ReadTimeSessionServerSummary> BuildReaderServerSummaries(
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

    private protected static List<ReadTimeSessionRequestSummary> BuildReaderRequestSummaries(
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

    private protected static HashSet<string> CollectReaderDescendantSpanIds(string rootSpanId, IReadOnlyList<CollectedRecord> records)
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

    private protected static string? ExtractReaderPromptPreview(
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

    private protected static bool HasReaderPromptParts(IReadOnlyList<CollectedContentPart> parts)
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

    private protected static string? ExtractReaderPromptFromParts(IReadOnlyList<CollectedContentPart> parts)
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

    private protected static string TruncateReaderSurrogateSafe(string text, int maxLength)
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

    private protected static List<SubagentSession> BuildReaderSubagentSummaries(
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

    private protected static ReadTimeAuxiliarySummary BuildReaderAuxiliarySummary(
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

    private protected static bool IsReaderAuxiliaryRecord(CollectedRecord record)
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

    private protected static ReadTimeContextBucketSummary BuildReaderContextBucketSummary(
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

    private protected static UnusedSchemaRange? ResolveReaderUnusedRange(
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
            UnusedSchemaRange? resolved = CanonPricing.IsCopilotHarness(harnessId)
                ? ResolveCopilotUnusedRange(primaryModel, residencies)
                : CanonPricing.IsPublishedTableHarness(harnessId) && !CanonPricing.IsFlatRateModel(primaryModel)
                    ? ResolvePublishedUnusedRange(primaryModel, residencies, pricing)
                    : null;
            if (resolved is not null)
            {
                return resolved;
            }
        }

        return new UnusedSchemaRange(residencies, 0, residencies, null);
    }

    private protected static UnusedSchemaRange? ResolveCopilotUnusedRange(string primaryModel, long residencies)
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

        return null;
    }

    private protected static UnusedSchemaRange? ResolvePublishedUnusedRange(
        string primaryModel,
        long residencies,
        IModelPricingLookup? pricing)
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

        return null;
    }

    /// <summary>Executes a parameterized read; a seam for observing the statements a read issues.</summary>
    /// <typeparam name="T">Mapped row type.</typeparam>
    /// <param name="sql">SQL text whose values are supplied as <c>$name</c> parameters.</param>
    /// <param name="parameters">Parameter values keyed by their <c>$name</c> marker.</param>
    /// <param name="map">Maps one row.</param>
    protected virtual IReadOnlyList<T> QueryRows<T>(
        string sql,
        IReadOnlyDictionary<string, object?> parameters,
        Func<SqliteDataReader, T> map)
    {
        ArgumentNullException.ThrowIfNull(sql);
        ArgumentNullException.ThrowIfNull(parameters);
        ArgumentNullException.ThrowIfNull(map);
        EnsureDatabaseDirectoryIfMissing();
        return Query(sql, parameters, map);
    }

    /// <summary>Binds one value to the query and returns its <c>$pN</c> marker.</summary>
    private protected static string Bind(Dictionary<string, object?> parameters, object? value)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        string name = "$p" + parameters.Count.ToString(CultureInfo.InvariantCulture);
        parameters.Add(name, value);
        return name;
    }

    private protected static int ParseInt32Required(string cell, string columnName)
    {
        if (int.TryParse(cell, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value))
        {
            return value;
        }

        throw new StoredDataException($"Invalid 32-bit integer value '{cell}' for '{columnName}'.");
    }

    private protected static bool ParseBoolFlag(string cell, string columnName) => cell switch
    {
        "0" => false,
        "1" => true,
        _ => throw new StoredDataException($"Invalid boolean flag '{cell}' for '{columnName}'."),
    };

    private protected static string? ReadNullableString(SqliteDataReader reader, string columnName)
    {
        int ordinal = reader.GetOrdinal(columnName);
        return reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
    }

    private protected static long? ReadNullableInt64(SqliteDataReader reader, string columnName)
    {
        int ordinal = reader.GetOrdinal(columnName);
        return reader.IsDBNull(ordinal) ? null : reader.GetInt64(ordinal);
    }

    private protected static double? ReadNullableDouble(SqliteDataReader reader, string columnName)
    {
        int ordinal = reader.GetOrdinal(columnName);
        return reader.IsDBNull(ordinal) ? null : reader.GetDouble(ordinal);
    }

    private protected static DateTimeOffset ParseTimestampRequired(string text, string columnName)
    {
        if (DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTimeOffset parsed))
        {
            return parsed.ToUniversalTime();
        }

        throw new StoredDataException($"Invalid ISO-8601 timestamp '{text}' for '{columnName}'.");
    }

    private protected static SourceKind ParseSourceKind(string value) => value switch
    {
        "file" => SourceKind.File,
        "otlp" => SourceKind.Otlp,
        _ => throw new StoredDataException($"Unrecognized source_kind '{value}'."),
    };

    private protected static RecordOp ParseRecordOp(string value) => value switch
    {
        "llm.invoke" => RecordOp.LlmInvoke,
        "tool.invoke" => RecordOp.ToolInvoke,
        "agent.invoke" => RecordOp.AgentInvoke,
        "unspecified" => RecordOp.Unspecified,
        _ => throw new StoredDataException($"Unrecognized record.op '{value}'."),
    };

    private protected static RecordKind ParseRecordKind(string value) => value switch
    {
        "unspecified" => RecordKind.Unspecified,
        "internal" => RecordKind.Internal,
        "server" => RecordKind.Server,
        "client" => RecordKind.Client,
        "producer" => RecordKind.Producer,
        "consumer" => RecordKind.Consumer,
        _ => throw new StoredDataException($"Unrecognized record.kind '{value}'."),
    };

    private protected static RecordStatus ParseRecordStatus(string value) => value switch
    {
        "unset" => RecordStatus.Unset,
        "ok" => RecordStatus.Ok,
        "error" => RecordStatus.Error,
        "unspecified" => RecordStatus.Unspecified,
        _ => throw new StoredDataException($"Unrecognized record.status '{value}'."),
    };

    private protected static TokenClass ParseTokenClass(string value) => value switch
    {
        "input" => TokenClass.Input,
        "cache_read" => TokenClass.CacheRead,
        "cache_write" => TokenClass.CacheWrite,
        "output" => TokenClass.Output,
        "reasoning" => TokenClass.Reasoning,
        "total" => TokenClass.Total,
        _ => throw new StoredDataException($"Unrecognized token_class '{value}'."),
    };

    private protected static ContentBucket ParseContentBucket(string value) => value switch
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

    private protected static ReportedTotalDirection ParseReportedDirection(string value) => value switch
    {
        "input" => ReportedTotalDirection.Input,
        "output" => ReportedTotalDirection.Output,
        _ => throw new StoredDataException($"Unrecognized record_reported_total.direction '{value}'."),
    };

    private protected static ProblemSeverity ParseProblemSeverityValue(string value)
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

    private protected static List<T> GetOrCreateList<TKey, T>(Dictionary<TKey, List<T>> map, TKey key)
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
    private protected static CostBlock PriceReportedTokens(
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

    private protected record RawRecordHeader(
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

    private protected record RawSessionHeader(
        string SessionId,
        string HarnessId,
        string NativeId,
        CollectedWorkspace? Workspace,
        string? Title);

    /// <summary>
    /// What a session's list figure takes from its projection: the number of request spans, which
    /// a figure prefers over its record-derived request count, and the stored turn entries.
    /// </summary>
    private protected record SessionListProjectionShape(
        int RequestSpanCount,
        IReadOnlyList<StoredSessionTurn> Turns);

    private protected record RawContextHeader(
        long ContextLimit,
        string ContextLimitSource,
        long ResidualTotal,
        double FreshJumpFactor,
        double AccumulationRate);

    private protected record RawFindingHeader(
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

    private protected record RawContextTurnHeader(
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

/// <summary>
/// Session and record reads over a SQLite relational store.
/// </summary>
/// <summary>
/// Record, projection, and aggregate queries over a SQLite relational store.
/// </summary>
public abstract class SqliteRelationalStoreReadQueryBase : SqliteRelationalStoreReadBase
{
    /// <summary>Initializes the query half over <paramref name="databasePath"/>.</summary>
    /// <param name="databasePath">Path to the SQLite database file.</param>
    /// <param name="circuitBreaker">Circuit breaker guarding every <c>sqlite3</c> invocation.</param>
    /// <param name="fileSystem">Filesystem seam; defaults to <see cref="PhysicalRelationalStoreFileSystem.Instance"/> when <see langword="null"/>.</param>
    protected SqliteRelationalStoreReadQueryBase(
        string databasePath,
        ICircuitBreaker circuitBreaker,
        IRelationalStoreFileSystem? fileSystem)
        : base(databasePath, circuitBreaker, fileSystem)
    {
    }

    /// <summary>
    /// Joins a record (alias <c>r</c>) to its source row. An inner join, because
    /// <c>record.source_id</c> is a required foreign key: a record always has a source, and one
    /// whose source is deleted is cascaded away with it.
    /// </summary>
    private protected const string RecordSourceJoin = "JOIN source src ON src.source_id = r.source_id ";

    /// <summary>Left-joins a record (alias <c>r</c>) to its optional response-id and request-id rows.</summary>
    private protected const string RecordJoinIdJoin =
        "LEFT JOIN record_response_id rrid ON rrid.span_id = r.span_id " +
        "LEFT JOIN record_request_id rqid ON rqid.span_id = r.span_id ";

    /// <summary>
    /// The projection header: every <c>session_projection</c> column and its five single-row
    /// satellites, with no child collection. The full projection read and the paged header reads
    /// select the same fifteen columns, so both parse one row shape.
    /// </summary>
    private protected const string SessionProjectionHeaderColumns =
        "sp.session_id, sp.label, sp.is_subagent, sp.span_count, " +
        "sp.started_at_utc, sp.ended_at_utc, " +
        "spp.parent_session_id, " +
        "spa.agent_name, " +
        "CASE WHEN spip.session_id IS NOT NULL THEN 1 ELSE 0 END, " +
        "scu.reason, " +
        "ssu.reason";

    /// <summary>The single-row satellites <see cref="SessionProjectionHeaderColumns"/> reads, joined on <c>sp.session_id</c>.</summary>
    private protected const string SessionProjectionHeaderJoins =
        "LEFT JOIN session_projection_parent spp ON spp.session_id = sp.session_id " +
        "LEFT JOIN session_projection_agent spa ON spa.session_id = sp.session_id " +
        "LEFT JOIN session_projection_included_in_parent spip ON spip.session_id = sp.session_id " +
        "LEFT JOIN session_context_unmeasurable scu ON scu.session_id = sp.session_id " +
        "LEFT JOIN session_schema_unmeasurable ssu ON ssu.session_id = sp.session_id";

    /// <summary>The scalar record columns the broad and content-free record reads select.</summary>
    private protected const string RecordReadColumns =
        "r.span_id, r.harness_id, r.op, r.kind, r.name, r.started_at_utc, r.status, " +
        "rsl.session_key, rp.parent_span_id, rt.trace_id, rm.model_id, re.ended_at_utc, rrd.duration_ms, " +
        "rrc.amount, rrc.currency, rai.agent_instance_id, rerr.message AS error_message, " +
        "ref.failure_count, rcr.correction_rule, rdcw.declared_context_window, " +
        "rrti.token_count AS reported_input, rrto.token_count AS reported_output, " +
        "rrid.response_id, rqid.request_id, " +
        "src.source_kind, src.harness_id AS source_harness_id, src.locator";

    /// <summary>The joins <see cref="RecordReadColumns"/> needs, from <c>record r</c>.</summary>
    private protected const string RecordReadFrom =
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
        "LEFT JOIN record_reported_total rrto ON rrto.span_id = r.span_id AND rrto.direction = 'output'";

    /// <summary>
    /// The sessions still standing after the root rule's last round: exactly the sessions on a
    /// parent chain that loops back to itself. No acyclic session outlives as many rounds as the
    /// store has projections.
    /// </summary>
    private protected const string CyclicSessionSet =
        "(SELECT session_id FROM keep WHERE round = (SELECT COUNT(*) FROM session_projection) + 1)";

    /// <summary>
    /// Reads the eleven projection header columns <see cref="SessionProjectionHeaderColumns"/>
    /// selects, starting at <paramref name="startIndex"/>.
    /// </summary>
    /// <param name="reader">Reader positioned on a header row.</param>
    /// <param name="startIndex">Index of the session id column.</param>
    /// <returns>The projection header.</returns>
    private protected static StoredSessionHeader ReadSessionHeader(SqliteDataReader reader, int startIndex) => new(
        reader.GetString(startIndex),
        reader.GetString(startIndex + 1),
        reader.GetInt64(startIndex + 2) == 1L,
        reader.GetInt32(startIndex + 3),
        reader.GetString(startIndex + 4),
        reader.GetString(startIndex + 5),
        reader.IsDBNull(startIndex + 6) ? null : reader.GetString(startIndex + 6),
        reader.IsDBNull(startIndex + 7) ? null : reader.GetString(startIndex + 7),
        reader.GetInt64(startIndex + 8) == 1L,
        reader.IsDBNull(startIndex + 9) ? null : reader.GetString(startIndex + 9),
        reader.IsDBNull(startIndex + 10) ? null : reader.GetString(startIndex + 10));

    /// <summary>Builds the <c>IN (…)</c> list that binds <paramref name="keys"/> as parameters.</summary>
    /// <param name="keys">Text keys to bind; never empty.</param>
    /// <param name="parameters">Parameter values the list binds.</param>
    /// <returns>The comma-separated parameter markers.</returns>
    private protected static string SessionKeyListFilter(IReadOnlyCollection<string> keys, Dictionary<string, object?> parameters) =>
        string.Join(", ", keys.Select(key => Bind(parameters, key)));

    private protected List<Problem> QuerySessionIngestProblems(string sessionKey)
    {
        Dictionary<string, List<Problem>> grouped = QuerySessionIngestProblemsBySession([sessionKey]);
        return grouped.TryGetValue(sessionKey, out List<Problem>? problems) ? problems : [];
    }

    private protected Dictionary<string, IReadOnlyList<StoredSessionTurn>> QuerySessionTurnsBySession(IReadOnlyList<string> sessionKeys)
    {
        Dictionary<string, IReadOnlyList<StoredSessionTurn>> turnsBySession = new(StringComparer.Ordinal);
        if (sessionKeys.Count == 0)
        {
            return turnsBySession;
        }

        Dictionary<string, object?> parameters = new(StringComparer.Ordinal);
        string filter = SessionKeyListFilter(sessionKeys, parameters);
        string sql =
            "SELECT session_id, position, span_id FROM session_turn " +
            $"WHERE session_id IN ({filter}) ORDER BY session_id ASC, position ASC";
        Dictionary<string, List<StoredSessionTurn>> grouped = new(StringComparer.Ordinal);
        foreach (StoredSessionTurn turn in QueryRows<StoredSessionTurn>(
                     sql,
                     parameters,
                     static reader => new StoredSessionTurn(
                         reader.GetString(reader.GetOrdinal("session_id")),
                         reader.GetInt32(reader.GetOrdinal("position")),
                         reader.GetString(reader.GetOrdinal("span_id")))))
        {
            GetOrCreateList(grouped, turn.SessionId).Add(turn);
        }

        foreach (KeyValuePair<string, List<StoredSessionTurn>> entry in grouped)
        {
            turnsBySession[entry.Key] = entry.Value;
        }

        return turnsBySession;
    }

    private protected Dictionary<string, List<Problem>> QuerySessionIngestProblemsBySession(IReadOnlyList<string> sessionKeys)
    {
        Dictionary<string, List<Problem>> problemsBySession = new(StringComparer.Ordinal);
        if (sessionKeys.Count == 0)
        {
            return problemsBySession;
        }

        Dictionary<string, object?> parameters = new(StringComparer.Ordinal);
        string filter = SessionKeyListFilter(sessionKeys, parameters);
        string sql =
            "SELECT att.session_key, " +
            "ip.severity, ip.code, ip.message, " +
            "ipl.location, ips.span_id AS problem_span_id, ipst.stack " +
            "FROM ingest_problem ip " +
            "LEFT JOIN ingest_problem_location ipl ON ipl.problem_id = ip.problem_id " +
            "LEFT JOIN ingest_problem_span ips ON ips.problem_id = ip.problem_id " +
            "LEFT JOIN ingest_problem_stack ipst ON ipst.problem_id = ip.problem_id " +
            "JOIN (" +
            $"SELECT session_id AS session_key, problem_id FROM ingest_problem_session WHERE session_id IN ({filter}) UNION " +
            $"SELECT rsl.session_key AS session_key, psp.problem_id FROM ingest_problem_span psp JOIN record_session_link rsl ON rsl.span_id = psp.span_id WHERE rsl.session_key IN ({filter})" +
            ") att ON att.problem_id = ip.problem_id " +
            "ORDER BY att.session_key ASC, ip.problem_id ASC";

        foreach ((string SessionId, Problem Problem) row in QueryRows<(string SessionId, Problem Problem)>(
                     sql,
                     parameters,
                     static reader =>
                     {
                         string? location = ReadNullableString(reader, "location") ?? ReadNullableString(reader, "problem_span_id");
                         return (
                             reader.GetString(reader.GetOrdinal("session_key")),
                             new Problem(
                                 ParseProblemSeverityValue(reader.GetString(reader.GetOrdinal("severity"))),
                                 reader.GetString(reader.GetOrdinal("code")),
                                 reader.GetString(reader.GetOrdinal("message")),
                                 location,
                                 ReadNullableString(reader, "stack")));
                     }))
        {
            GetOrCreateList(problemsBySession, row.SessionId).Add(row.Problem);
        }

        return problemsBySession;
    }

    /// <summary>
    /// Reads one record row of a record query into a raw record header — the shared parse behind
    /// both the broad record read and the content-free sessions-list read, so both derive from the
    /// same scalar columns.
    /// </summary>
    /// <param name="reader">Reader positioned on a <see cref="RecordReadColumns"/> row.</param>
    private protected static RawRecordHeader ReadRawRecordHeader(SqliteDataReader reader)
    {
        string? endedRaw = ReadNullableString(reader, "ended_at_utc");
        DateTimeOffset? endedAt = endedRaw is null ? null : ParseTimestampRequired(endedRaw, "record_end.ended_at_utc");
        double? costAmount = ReadNullableDouble(reader, "amount");
        string? costCurrency = ReadNullableString(reader, "currency");
        CollectedCost? reportedCost = costAmount.HasValue && costCurrency is not null
            ? new CollectedCost(costAmount.Value, costCurrency)
            : costAmount.HasValue || costCurrency is not null
                ? throw new StoredDataException("Partial record_reported_cost row returned.")
                : null;

        return new RawRecordHeader(
            reader.GetString(reader.GetOrdinal("span_id")),
            reader.GetString(reader.GetOrdinal("harness_id")),
            ParseRecordOp(reader.GetString(reader.GetOrdinal("op"))),
            ParseRecordKind(reader.GetString(reader.GetOrdinal("kind"))),
            reader.GetString(reader.GetOrdinal("name")),
            ParseTimestampRequired(reader.GetString(reader.GetOrdinal("started_at_utc")), "record.started_at_utc"),
            ParseRecordStatus(reader.GetString(reader.GetOrdinal("status"))),
            ReadNullableString(reader, "session_key"),
            ReadNullableString(reader, "parent_span_id"),
            ReadNullableString(reader, "trace_id"),
            ReadNullableString(reader, "model_id"),
            endedAt,
            ReadNullableInt64(reader, "duration_ms"),
            reportedCost,
            ReadNullableString(reader, "agent_instance_id"),
            new CollectedRecordSignals(
                ReadNullableString(reader, "error_message"),
                ReadNullableInt64(reader, "failure_count"),
                ReadNullableString(reader, "correction_rule"),
                ReadNullableInt64(reader, "declared_context_window")),
            ReadNullableInt64(reader, "reported_input"),
            ReadNullableInt64(reader, "reported_output"),
            ReadRecordSourceName(reader),
            ReadNullableString(reader, "response_id"),
            ReadNullableString(reader, "request_id"));
    }

    /// <summary>
    /// Reads the three <see cref="RecordSourceColumns"/> of a record row into the record's source
    /// name as the projection rules read it: the locator for an OTLP source, or
    /// <see cref="CanonSynth.FileSourcePrefix"/> followed by the source's harness for a file source.
    /// </summary>
    /// <remarks>
    /// The name comes from the stored <c>source</c> row alone, never from record attributes, so a
    /// record's source is exactly the batch source it was written under. The file form uses the
    /// same prefix constant the file synthesizer stamps, which is what the projector's file-source
    /// test reads.
    /// </remarks>
    /// <param name="reader">Reader positioned on a <see cref="RecordReadColumns"/> row.</param>
    private protected static string ReadRecordSourceName(SqliteDataReader reader)
    {
        SourceKind kind = ParseSourceKind(reader.GetString(reader.GetOrdinal("source_kind")));
        string harnessId = reader.GetString(reader.GetOrdinal("source_harness_id"));
        string locator = reader.GetString(reader.GetOrdinal("locator"));
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
    private protected Dictionary<string, List<CollectedRecord>> QueryContentFreeRecordsBySession(
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
        Dictionary<string, object?> parameters = new(StringComparer.Ordinal);
        string? spanMarker = scopedSpanId is null ? null : Bind(parameters, scopedSpanId);
        string keyFilter = SessionKeyListFilter(sessionKeys, parameters);
        string recordFilter = spanMarker is null
            ? $" WHERE rsl.session_key IN ({keyFilter})"
            : $" WHERE r.span_id = {spanMarker}";
        string tokenFilter = spanMarker is null
            ? $" WHERE r.span_id IN (SELECT rsl.span_id FROM record_session_link rsl WHERE rsl.session_key IN ({keyFilter}))"
            : $" WHERE r.span_id = {spanMarker}";
        string linkedSpanIds = spanMarker is null
            ? $"(SELECT rsl.span_id FROM record_session_link rsl WHERE rsl.session_key IN ({keyFilter}))"
            : $"({spanMarker})";

        string recordSql = "SELECT " + RecordReadColumns + " " + RecordReadFrom + recordFilter + " ORDER BY r.rowid ASC";
        List<RawRecordHeader> rawRecords = [.. Query(recordSql, parameters, ReadRawRecordHeader)];

        Dictionary<string, List<CollectedTokenCount>> tokensBySpan = new(StringComparer.Ordinal);
        foreach ((string SpanId, TokenClass TokenClass, long Count) row in QueryRows<(string SpanId, TokenClass TokenClass, long Count)>(
                     "SELECT r.span_id, r.token_class, r.token_count " +
                     $"FROM record_token_count r{tokenFilter} ORDER BY r.rowid ASC",
                     parameters,
                     static reader => (
                         reader.GetString(reader.GetOrdinal("span_id")),
                         ParseTokenClass(reader.GetString(reader.GetOrdinal("token_class"))),
                         reader.GetInt64(reader.GetOrdinal("token_count")))))
        {
            GetOrCreateList(tokensBySpan, row.SpanId).Add(new CollectedTokenCount(row.TokenClass, row.Count));
        }

        Dictionary<string, List<CollectedToolDefinition>> defsBySpan = new(StringComparer.Ordinal);
        Dictionary<string, CollectedToolCall> toolCallsBySpan = new(StringComparer.Ordinal);
        if (includeToolMetadata)
        {
            foreach ((string SpanId, CollectedToolDefinition Definition) row in QueryRows<(string SpanId, CollectedToolDefinition Definition)>(
                         "SELECT td.span_id, td.position, td.tool_name, tds.server_name, td.schema_tokens, " +
                         "tdt.tokenizer, tdt.is_model_own " +
                         "FROM tool_definition td " +
                         "LEFT JOIN tool_definition_server tds ON tds.span_id = td.span_id AND tds.position = td.position " +
                         "LEFT JOIN tool_definition_tokenizer tdt ON tdt.span_id = td.span_id AND tdt.position = td.position" +
                         $" WHERE td.span_id IN {linkedSpanIds} ORDER BY td.span_id ASC, td.position ASC",
                         parameters,
                         static reader => (
                             reader.GetString(reader.GetOrdinal("span_id")),
                             new CollectedToolDefinition(
                                 reader.GetInt32(reader.GetOrdinal("position")),
                                 reader.GetString(reader.GetOrdinal("tool_name")),
                                 ReadNullableString(reader, "server_name"),
                                 reader.GetInt64(reader.GetOrdinal("schema_tokens")),
                                 ReadNullableString(reader, "tokenizer"),
                                 !reader.IsDBNull(reader.GetOrdinal("is_model_own")) && reader.GetInt64(reader.GetOrdinal("is_model_own")) == 1L))))
            {
                GetOrCreateList(defsBySpan, row.SpanId).Add(row.Definition);
            }

            // The tool-call metadata joins deliberately omit tool_call_input_text and
            // tool_call_result_text: sizes, fingerprints, outcomes, errors, and exit codes are
            // metadata, and those two tables hold the only tool content.
            string toolCallSql =
                "SELECT tc.span_id, " + BuildToolCallMetadataColumns("tc") +
                " FROM tool_call tc " +
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
                $" WHERE tc.span_id IN {linkedSpanIds} ORDER BY tc.rowid ASC";
            foreach ((string SpanId, CollectedToolCall Call) row in QueryRows<(string SpanId, CollectedToolCall Call)>(
                         toolCallSql,
                         parameters,
                         static reader => (reader.GetString(reader.GetOrdinal("span_id")), ReadToolCallColumns(reader))))
            {
                toolCallsBySpan[row.SpanId] = row.Call;
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

    private protected IReadOnlyList<CollectedRecord> QueryRecords(string? wherePredicate, Dictionary<string, object?> parameters)
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

        string recordSql = "SELECT " + RecordReadColumns + " " + RecordReadFrom + recordFilter + " ORDER BY r.rowid ASC";
        List<RawRecordHeader> rawRecords = [.. Query(recordSql, parameters, ReadRawRecordHeader)];

        Dictionary<string, List<CollectedTokenCount>> tokensBySpan = new(StringComparer.Ordinal);
        foreach ((string SpanId, TokenClass TokenClass, long Count) row in QueryRows<(string SpanId, TokenClass TokenClass, long Count)>(
                     "SELECT r.span_id, r.token_class, r.token_count " +
                     $"FROM record_token_count r{childSpanFilter} ORDER BY r.rowid ASC",
                     parameters,
                     static reader => (
                         reader.GetString(reader.GetOrdinal("span_id")),
                         ParseTokenClass(reader.GetString(reader.GetOrdinal("token_class"))),
                         reader.GetInt64(reader.GetOrdinal("token_count")))))
        {
            GetOrCreateList(tokensBySpan, row.SpanId).Add(new CollectedTokenCount(row.TokenClass, row.Count));
        }

        Dictionary<string, List<string>> skillsBySpan = new(StringComparer.Ordinal);
        foreach ((string SpanId, string SkillName) row in QueryRows<(string SpanId, string SkillName)>(
                     "SELECT r.span_id, r.skill_name " +
                     $"FROM record_skill_invocation r{childSpanFilter} ORDER BY r.rowid ASC",
                     parameters,
                     static reader => (reader.GetString(0), reader.GetString(1))))
        {
            GetOrCreateList(skillsBySpan, row.SpanId).Add(row.SkillName);
        }

        Dictionary<string, List<string>> listedBySpan = new(StringComparer.Ordinal);
        foreach ((string SpanId, string SkillName) row in QueryRows<(string SpanId, string SkillName)>(
                     "SELECT r.span_id, r.skill_name " +
                     $"FROM record_skill_listing r{childSpanFilter} ORDER BY r.span_id ASC, r.position ASC",
                     parameters,
                     static reader => (reader.GetString(0), reader.GetString(1))))
        {
            GetOrCreateList(listedBySpan, row.SpanId).Add(row.SkillName);
        }

        Dictionary<string, List<CollectedContentPart>> partsBySpan = new(StringComparer.Ordinal);
        string partSql =
            "SELECT cp.span_id, " + BuildContentPartColumns("cp") +
            " FROM content_part cp " +
            "LEFT JOIN content_part_text cpt ON cpt.span_id = cp.span_id AND cpt.position = cp.position " +
            "LEFT JOIN content_part_withheld cpw ON cpw.span_id = cp.span_id AND cpw.position = cp.position " +
            "LEFT JOIN content_part_withheld_tokenizer cpwt ON cpwt.span_id = cp.span_id AND cpwt.position = cp.position " +
            "LEFT JOIN content_part_reported_tokens cprt ON cprt.span_id = cp.span_id AND cprt.position = cp.position " +
            "LEFT JOIN content_part_server cps ON cps.span_id = cp.span_id AND cps.position = cp.position " +
            "LEFT JOIN content_part_role cpr ON cpr.span_id = cp.span_id AND cpr.position = cp.position " +
            "LEFT JOIN content_part_residency cpres ON cpres.span_id = cp.span_id AND cpres.position = cp.position " +
            "LEFT JOIN content_part_truncation cptr ON cptr.span_id = cp.span_id AND cptr.position = cp.position" +
            $"{partFilter} ORDER BY cp.span_id ASC, cp.position ASC";
        foreach ((string SpanId, CollectedContentPart Part) row in QueryRows<(string SpanId, CollectedContentPart Part)>(
                     partSql,
                     parameters,
                     static reader => (reader.GetString(reader.GetOrdinal("span_id")), ReadContentPartColumns(reader))))
        {
            GetOrCreateList(partsBySpan, row.SpanId).Add(row.Part);
        }

        Dictionary<string, List<CollectedToolDefinition>> defsBySpan = new(StringComparer.Ordinal);
        foreach ((string SpanId, CollectedToolDefinition Definition) row in QueryRows<(string SpanId, CollectedToolDefinition Definition)>(
                     "SELECT td.span_id, td.position, td.tool_name, tds.server_name, td.schema_tokens, " +
                     "tdt.tokenizer, tdt.is_model_own " +
                     "FROM tool_definition td " +
                     "LEFT JOIN tool_definition_server tds ON tds.span_id = td.span_id AND tds.position = td.position " +
                     "LEFT JOIN tool_definition_tokenizer tdt ON tdt.span_id = td.span_id AND tdt.position = td.position" +
                     $"{defFilter} ORDER BY td.span_id ASC, td.position ASC",
                     parameters,
                     static reader => (
                         reader.GetString(reader.GetOrdinal("span_id")),
                         new CollectedToolDefinition(
                             reader.GetInt32(reader.GetOrdinal("position")),
                             reader.GetString(reader.GetOrdinal("tool_name")),
                             ReadNullableString(reader, "server_name"),
                             reader.GetInt64(reader.GetOrdinal("schema_tokens")),
                             ReadNullableString(reader, "tokenizer"),
                             !reader.IsDBNull(reader.GetOrdinal("is_model_own")) && reader.GetInt64(reader.GetOrdinal("is_model_own")) == 1L))))
        {
            GetOrCreateList(defsBySpan, row.SpanId).Add(row.Definition);
        }

        Dictionary<string, CollectedToolCall> toolCallsBySpan = new(StringComparer.Ordinal);
        string toolCallSql =
            "SELECT tc.span_id, " + BuildToolCallColumns("tc") +
            " FROM tool_call tc " +
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
            $"{tcFilter} ORDER BY tc.rowid ASC";
        foreach ((string SpanId, CollectedToolCall Call) row in QueryRows<(string SpanId, CollectedToolCall Call)>(
                     toolCallSql,
                     parameters,
                     static reader => (reader.GetString(reader.GetOrdinal("span_id")), ReadToolCallColumns(reader))))
        {
            toolCallsBySpan[row.SpanId] = row.Call;
        }

        Dictionary<string, CollectedAgentInvocation> agentCallsBySpan = new(StringComparer.Ordinal);
        string agentSql =
            "SELECT ai.span_id, " + BuildAgentInvocationColumns("ai") +
            " FROM agent_invocation ai " +
            "LEFT JOIN agent_invocation_spawn ais ON ais.span_id = ai.span_id " +
            "LEFT JOIN agent_invocation_outcome aio ON aio.span_id = ai.span_id " +
            "LEFT JOIN agent_invocation_error aie ON aie.span_id = ai.span_id" +
            $"{aiFilter} ORDER BY ai.rowid ASC";
        foreach ((string SpanId, CollectedAgentInvocation Invocation) row in QueryRows<(string SpanId, CollectedAgentInvocation Invocation)>(
                     agentSql,
                     parameters,
                     static reader => (reader.GetString(reader.GetOrdinal("span_id")), ReadAgentInvocationColumns(reader))))
        {
            agentCallsBySpan[row.SpanId] = row.Invocation;
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
                header.RequestId,
                // No listing rows reads back as null: the store cannot tell "listed nothing" from "no listing".
                listedBySpan.GetValueOrDefault(header.SpanId)));
        }

        return materialized;
    }

    private protected IReadOnlyList<CollectedSession> QuerySessions(string? sessionPredicate, string? usagePredicate, Dictionary<string, object?> parameters)
    {
        string sessionFilter = sessionPredicate is null ? string.Empty : $" WHERE {sessionPredicate}";
        string usageFilter = usagePredicate is null ? string.Empty : $" WHERE {usagePredicate}";

        string sessionSql =
            "SELECT s.session_id, s.harness_id, s.native_session_id, " +
            "sw.working_directory, sr.repo_name, sb.branch_name, st.title " +
            "FROM session s " +
            "LEFT JOIN session_workspace sw ON sw.session_id = s.session_id " +
            "LEFT JOIN session_repository sr ON sr.session_id = s.session_id " +
            "LEFT JOIN session_branch sb ON sb.session_id = s.session_id " +
            "LEFT JOIN session_title st ON st.session_id = s.session_id" +
            $"{sessionFilter} ORDER BY s.rowid ASC";
        List<RawSessionHeader> headers = [.. QueryRows<RawSessionHeader>(
            sessionSql,
            parameters,
            static reader =>
            {
                string? workingDir = ReadNullableString(reader, "working_directory");
                string? repo = ReadNullableString(reader, "repo_name");
                string? branch = ReadNullableString(reader, "branch_name");
                CollectedWorkspace? workspace = workingDir is null && repo is null && branch is null
                    ? null
                    : new CollectedWorkspace(workingDir, repo, branch);
                return new RawSessionHeader(
                    reader.GetString(reader.GetOrdinal("session_id")),
                    reader.GetString(reader.GetOrdinal("harness_id")),
                    reader.GetString(reader.GetOrdinal("native_session_id")),
                    workspace,
                    ReadNullableString(reader, "title"));
            })];

        Dictionary<string, List<CollectedModelUsage>> usagesBySession = new(StringComparer.Ordinal);
        string usageSql =
            "SELECT u.session_id, u.snapshot_position, u.model_id, u.token_class, u.token_count " +
            $"FROM session_model_usage u{usageFilter} ORDER BY u.session_id ASC, u.snapshot_position ASC, u.model_id ASC, u.token_class ASC";
        foreach ((string SessionId, CollectedModelUsage Usage) row in QueryRows<(string SessionId, CollectedModelUsage Usage)>(
                     usageSql,
                     parameters,
                     static reader => (
                         reader.GetString(reader.GetOrdinal("session_id")),
                         new CollectedModelUsage(
                             reader.GetInt32(reader.GetOrdinal("snapshot_position")),
                             reader.GetString(reader.GetOrdinal("model_id")),
                             ParseTokenClass(reader.GetString(reader.GetOrdinal("token_class"))),
                             reader.GetInt64(reader.GetOrdinal("token_count"))))))
        {
            GetOrCreateList(usagesBySession, row.SessionId).Add(row.Usage);
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

    private protected IReadOnlyList<StoredSessionProjection> QuerySessionProjections(string? filterPredicate, Dictionary<string, object?> parameters)
    {
        string projWhere = filterPredicate is null ? string.Empty : $" WHERE {filterPredicate}";
        string subSelect = filterPredicate is null
            ? "SELECT session_id FROM session_projection"
            : $"SELECT sp.session_id FROM session_projection sp WHERE {filterPredicate}";
        string childWhere = filterPredicate is null ? string.Empty : $" WHERE session_id IN ({subSelect})";
        string childAliasedWhere = filterPredicate is null ? string.Empty : $" WHERE t.session_id IN ({subSelect})";

        List<StoredSessionHeader> projHeaders = [.. QueryRows<StoredSessionHeader>(
            "SELECT " + SessionProjectionHeaderColumns + " " +
            "FROM session_projection sp " + SessionProjectionHeaderJoins +
            $"{projWhere} ORDER BY sp.started_at_utc DESC, sp.rowid ASC",
            parameters,
            static reader => ReadSessionHeader(reader, 0))];

        Dictionary<string, List<string>> requestsBySess = new(StringComparer.Ordinal);
        foreach ((string SessionId, string RequestSpanId) row in QueryRows<(string SessionId, string RequestSpanId)>(
                     $"SELECT session_id, request_span_id FROM session_request{childWhere} ORDER BY session_id ASC, position ASC",
                     parameters,
                     static reader => (reader.GetString(0), reader.GetString(1))))
        {
            GetOrCreateList(requestsBySess, row.SessionId).Add(row.RequestSpanId);
        }

        Dictionary<string, List<StoredSessionTurn>> turnsBySess = new(StringComparer.Ordinal);
        foreach ((string SessionId, int Position, string SpanId) row in QueryRows<(string SessionId, int Position, string SpanId)>(
                     $"SELECT session_id, position, span_id FROM session_turn{childWhere} ORDER BY session_id ASC, position ASC",
                     parameters,
                     static reader => (reader.GetString(0), reader.GetInt32(1), reader.GetString(2))))
        {
            GetOrCreateList(turnsBySess, row.SessionId).Add(new StoredSessionTurn(row.SessionId, row.Position, row.SpanId));
        }

        Dictionary<string, RawContextHeader> ctxHeadersBySess = new(StringComparer.Ordinal);
        foreach ((string SessionId, RawContextHeader Header) row in QueryRows<(string SessionId, RawContextHeader Header)>(
                     $"SELECT session_id, context_limit, context_limit_source, residual_total, fresh_jump_factor, accumulation_rate FROM session_context{childWhere}",
                     parameters,
                     static reader => (
                         reader.GetString(reader.GetOrdinal("session_id")),
                         new RawContextHeader(
                             reader.GetInt64(reader.GetOrdinal("context_limit")),
                             reader.GetString(reader.GetOrdinal("context_limit_source")),
                             reader.GetInt64(reader.GetOrdinal("residual_total")),
                             reader.GetDouble(reader.GetOrdinal("fresh_jump_factor")),
                             reader.GetDouble(reader.GetOrdinal("accumulation_rate"))))))
        {
            ctxHeadersBySess[row.SessionId] = row.Header;
        }

        Dictionary<string, List<string>> obsBucketsBySess = new(StringComparer.Ordinal);
        foreach ((string SessionId, string Bucket) row in QueryRows<(string SessionId, string Bucket)>(
                     $"SELECT session_id, bucket FROM session_context_observed_part{childWhere} ORDER BY session_id ASC, rowid ASC",
                     parameters,
                     static reader => (reader.GetString(0), reader.GetString(1))))
        {
            GetOrCreateList(obsBucketsBySess, row.SessionId).Add(row.Bucket);
        }

        Dictionary<string, List<StoredSessionContextDerivedCount>> derivedBySess = new(StringComparer.Ordinal);
        foreach ((string SessionId, StoredSessionContextDerivedCount Count) row in QueryRows<(string SessionId, StoredSessionContextDerivedCount Count)>(
                     $"SELECT session_id, counter, count FROM session_context_derived_count{childWhere} ORDER BY session_id ASC, rowid ASC",
                     parameters,
                     static reader => (
                         reader.GetString(reader.GetOrdinal("session_id")),
                         new StoredSessionContextDerivedCount(
                             reader.GetString(reader.GetOrdinal("counter")),
                             reader.GetInt64(reader.GetOrdinal("count"))))))
        {
            GetOrCreateList(derivedBySess, row.SessionId).Add(row.Count);
        }

        Dictionary<string, List<int>> flaggedBySess = new(StringComparer.Ordinal);
        foreach ((string SessionId, int TurnIndex) row in QueryRows<(string SessionId, int TurnIndex)>(
                     $"SELECT session_id, turn_index FROM session_context_flagged_turn{childWhere} ORDER BY session_id ASC, turn_index ASC",
                     parameters,
                     static reader => (reader.GetString(0), reader.GetInt32(1))))
        {
            GetOrCreateList(flaggedBySess, row.SessionId).Add(row.TurnIndex);
        }

        Dictionary<string, List<RawContextTurnHeader>> ctxTurnsBySess = new(StringComparer.Ordinal);
        string ctxTurnSql =
            "SELECT t.session_id, t.position, t.input_tokens, t.bucketed_tokens, t.residual_tokens, " +
            "t.residual_attribution, t.builtin_tool_definition_tokens, t.fresh_input, " +
            "h.headroom, h.pressure, a.rate, si.block_count, si.tokens, " +
            "fj.previous_fresh_input, fj.factor, nr.tokens AS non_resident_tokens " +
            "FROM session_context_turn t " +
            "LEFT JOIN session_context_turn_headroom h ON h.session_id = t.session_id AND h.position = t.position " +
            "LEFT JOIN session_context_turn_accumulation a ON a.session_id = t.session_id AND a.position = t.position " +
            "LEFT JOIN session_context_turn_stripped_instructions si ON si.session_id = t.session_id AND si.position = t.position " +
            "LEFT JOIN session_context_turn_fresh_jump fj ON fj.session_id = t.session_id AND fj.position = t.position " +
            "LEFT JOIN session_context_turn_non_resident nr ON nr.session_id = t.session_id AND nr.position = t.position" +
            $"{childAliasedWhere} ORDER BY t.session_id ASC, t.position ASC";
        foreach ((string SessionId, RawContextTurnHeader Header) row in QueryRows<(string SessionId, RawContextTurnHeader Header)>(
                     ctxTurnSql,
                     parameters,
                     static reader => (
                         reader.GetString(reader.GetOrdinal("session_id")),
                         new RawContextTurnHeader(
                             reader.GetInt32(reader.GetOrdinal("position")),
                             reader.GetInt64(reader.GetOrdinal("input_tokens")),
                             reader.GetInt64(reader.GetOrdinal("bucketed_tokens")),
                             reader.GetInt64(reader.GetOrdinal("residual_tokens")),
                             reader.GetString(reader.GetOrdinal("residual_attribution")),
                             reader.GetInt64(reader.GetOrdinal("builtin_tool_definition_tokens")),
                             reader.GetInt64(reader.GetOrdinal("fresh_input")),
                             ReadNullableInt64(reader, "headroom"),
                             ReadNullableDouble(reader, "pressure"),
                             ReadNullableDouble(reader, "rate"),
                             ReadNullableInt64(reader, "block_count"),
                             ReadNullableInt64(reader, "tokens"),
                             ReadNullableInt64(reader, "previous_fresh_input"),
                             ReadNullableDouble(reader, "factor"),
                             ReadNullableInt64(reader, "non_resident_tokens")))))
        {
            GetOrCreateList(ctxTurnsBySess, row.SessionId).Add(row.Header);
        }

        Dictionary<(string SessionId, int Position), List<StoredTurnBucketTokens>> ctxBucketsByTurn = [];
        foreach ((string SessionId, int Position, StoredTurnBucketTokens Bucket) row in QueryRows<(string SessionId, int Position, StoredTurnBucketTokens Bucket)>(
                     $"SELECT session_id, position, bucket, tokens FROM session_context_turn_bucket{childWhere} ORDER BY session_id ASC, position ASC, rowid ASC",
                     parameters,
                     static reader => (
                         reader.GetString(reader.GetOrdinal("session_id")),
                         reader.GetInt32(reader.GetOrdinal("position")),
                         new StoredTurnBucketTokens(
                             reader.GetString(reader.GetOrdinal("bucket")),
                             reader.GetInt64(reader.GetOrdinal("tokens"))))))
        {
            GetOrCreateList(ctxBucketsByTurn, (row.SessionId, row.Position)).Add(row.Bucket);
        }

        Dictionary<(string SessionId, int Position), List<StoredTurnServerDefinitionTokens>> ctxServersByTurn = [];
        foreach ((string SessionId, int Position, StoredTurnServerDefinitionTokens Server) row in QueryRows<(string SessionId, int Position, StoredTurnServerDefinitionTokens Server)>(
                     $"SELECT session_id, position, server, tokens FROM session_context_turn_server_definitions{childWhere} ORDER BY session_id ASC, position ASC, rowid ASC",
                     parameters,
                     static reader => (
                         reader.GetString(reader.GetOrdinal("session_id")),
                         reader.GetInt32(reader.GetOrdinal("position")),
                         new StoredTurnServerDefinitionTokens(
                             reader.GetString(reader.GetOrdinal("server")),
                             reader.GetInt64(reader.GetOrdinal("tokens"))))))
        {
            GetOrCreateList(ctxServersByTurn, (row.SessionId, row.Position)).Add(row.Server);
        }

        Dictionary<(string SessionId, int Position), List<TokenizerLabel>> ctxTokenizersByTurn = [];
        foreach ((string SessionId, int Position, TokenizerLabel Label) row in QueryRows<(string SessionId, int Position, TokenizerLabel Label)>(
                     $"SELECT session_id, position, tokenizer, is_model_own FROM session_context_turn_tokenizer{childWhere} ORDER BY session_id ASC, position ASC, tokenizer ASC",
                     parameters,
                     static reader => (
                         reader.GetString(reader.GetOrdinal("session_id")),
                         reader.GetInt32(reader.GetOrdinal("position")),
                         new TokenizerLabel(
                             reader.GetString(reader.GetOrdinal("tokenizer")),
                             reader.GetInt64(reader.GetOrdinal("is_model_own")) == 1L))))
        {
            GetOrCreateList(ctxTokenizersByTurn, (row.SessionId, row.Position)).Add(row.Label);
        }

        Dictionary<string, (int InvocationCount, int TurnCount, long? Unused)> schemaHeadersBySess = new(StringComparer.Ordinal);
        foreach ((string SessionId, int InvocationCount, int TurnCount, long? Unused) row in QueryRows<(string SessionId, int InvocationCount, int TurnCount, long? Unused)>(
                     "SELECT t.session_id, t.invocation_count, t.turn_count, u.token_residencies " +
                     "FROM session_schema t " +
                     $"LEFT JOIN session_schema_unused u ON u.session_id = t.session_id{childAliasedWhere}",
                     parameters,
                     static reader => (
                         reader.GetString(reader.GetOrdinal("session_id")),
                         reader.GetInt32(reader.GetOrdinal("invocation_count")),
                         reader.GetInt32(reader.GetOrdinal("turn_count")),
                         ReadNullableInt64(reader, "token_residencies"))))
        {
            schemaHeadersBySess[row.SessionId] = (row.InvocationCount, row.TurnCount, row.Unused);
        }

        Dictionary<string, List<StoredSchemaRankedTool>> rankedToolsBySess = new(StringComparer.Ordinal);
        foreach ((string SessionId, StoredSchemaRankedTool Tool) row in QueryRows<(string SessionId, StoredSchemaRankedTool Tool)>(
                     "SELECT t.session_id, t.position, t.tool_name, t.cost_tokens, t.invoked, s.server_name " +
                     "FROM session_schema_ranked_tool t " +
                     $"LEFT JOIN session_schema_ranked_tool_server s ON s.session_id = t.session_id AND s.position = t.position{childAliasedWhere} ORDER BY t.session_id ASC, t.position ASC",
                     parameters,
                     static reader => (
                         reader.GetString(reader.GetOrdinal("session_id")),
                         new StoredSchemaRankedTool(
                             reader.GetInt32(reader.GetOrdinal("position")),
                             reader.GetString(reader.GetOrdinal("tool_name")),
                             reader.GetInt64(reader.GetOrdinal("cost_tokens")),
                             reader.GetInt64(reader.GetOrdinal("invoked")) == 1L,
                             ReadNullableString(reader, "server_name")))))
        {
            GetOrCreateList(rankedToolsBySess, row.SessionId).Add(row.Tool);
        }

        Dictionary<string, List<StoredSchemaServerTokens>> schemaServersBySess = new(StringComparer.Ordinal);
        foreach ((string SessionId, StoredSchemaServerTokens Server) row in QueryRows<(string SessionId, StoredSchemaServerTokens Server)>(
                     $"SELECT session_id, server_name, tokens FROM session_schema_server{childWhere} ORDER BY session_id ASC, rowid ASC",
                     parameters,
                     static reader => (
                         reader.GetString(reader.GetOrdinal("session_id")),
                         new StoredSchemaServerTokens(
                             reader.GetString(reader.GetOrdinal("server_name")),
                             reader.GetInt64(reader.GetOrdinal("tokens"))))))
        {
            GetOrCreateList(schemaServersBySess, row.SessionId).Add(row.Server);
        }

        Dictionary<string, List<string>> subagentsBySess = new(StringComparer.Ordinal);
        foreach ((string SessionId, string SpanId) row in QueryRows<(string SessionId, string SpanId)>(
                     $"SELECT session_id, span_id FROM session_timeline_subagent{childWhere} ORDER BY session_id ASC, rowid ASC",
                     parameters,
                     static reader => (reader.GetString(0), reader.GetString(1))))
        {
            GetOrCreateList(subagentsBySess, row.SessionId).Add(row.SpanId);
        }

        Dictionary<string, List<string>> auxiliariesBySess = new(StringComparer.Ordinal);
        foreach ((string SessionId, string SpanId) row in QueryRows<(string SessionId, string SpanId)>(
                     $"SELECT session_id, span_id FROM session_timeline_auxiliary{childWhere} ORDER BY session_id ASC, rowid ASC",
                     parameters,
                     static reader => (reader.GetString(0), reader.GetString(1))))
        {
            GetOrCreateList(auxiliariesBySess, row.SessionId).Add(row.SpanId);
        }

        SessionProjectionRowSets rows = new(
            requestsBySess,
            turnsBySess,
            ctxHeadersBySess,
            obsBucketsBySess,
            derivedBySess,
            flaggedBySess,
            ctxTurnsBySess,
            ctxBucketsByTurn,
            ctxServersByTurn,
            ctxTokenizersByTurn,
            schemaHeadersBySess,
            rankedToolsBySess,
            schemaServersBySess,
            subagentsBySess,
            auxiliariesBySess);
        return BuildSessionProjections(projHeaders, rows);
    }

    private protected static List<StoredSessionProjection> BuildSessionProjections(
        List<StoredSessionHeader> projHeaders,
        SessionProjectionRowSets rows)
    {
        List<StoredSessionProjection> projections = new(projHeaders.Count);
        foreach (StoredSessionHeader header in projHeaders)
        {
            StoredSessionContext? context = null;
            if (rows.CtxHeadersBySess.TryGetValue(header.SessionId, out RawContextHeader? ctxHeader))
            {
                List<RawContextTurnHeader> rawTurns = rows.CtxTurnsBySess.TryGetValue(header.SessionId, out List<RawContextTurnHeader>? ct)
                    ? ct
                    : [];
                List<StoredSessionContextTurn> ctxTurns = new(rawTurns.Count);
                foreach (RawContextTurnHeader rt in rawTurns)
                {
                    (string, int) turnKey = (header.SessionId, rt.Position);
                    // A turn with no label rows means its figures came only from harness-reported
                    // counts — the schema-29 rebuild leaves no unlabelled measured row — so it is
                    // shown as reported, never as not measured.
                    ctxTurns.Add(new StoredSessionContextTurn(
                        rt.Position,
                        rt.InputTokens,
                        rt.BucketedTokens,
                        rt.ResidualTokens,
                        rt.ResidualAttribution,
                        rt.BuiltinToolDefinitionTokens,
                        rt.FreshInput,
                        rows.CtxBucketsByTurn.TryGetValue(turnKey, out List<StoredTurnBucketTokens>? bkts) ? bkts : [],
                        rows.CtxServersByTurn.TryGetValue(turnKey, out List<StoredTurnServerDefinitionTokens>? srvs) ? srvs : [],
                        rt.Headroom,
                        rt.Pressure,
                        rt.AccumulationRate,
                        rt.StrippedBlockCount,
                        rt.StrippedTokens,
                        rt.PreviousFreshInput,
                        rt.FreshJumpFactor,
                        rt.NonResidentTokens)
                    {
                        Tokenizers = rows.CtxTokenizersByTurn.TryGetValue(turnKey, out List<TokenizerLabel>? labels) ? labels : [],
                    });
                }

                context = new StoredSessionContext(
                    ctxHeader.ContextLimit,
                    ctxHeader.ContextLimitSource,
                    ctxHeader.ResidualTotal,
                    ctxHeader.FreshJumpFactor,
                    ctxHeader.AccumulationRate,
                    rows.ObsBucketsBySess.TryGetValue(header.SessionId, out List<string>? obs) ? obs : [],
                    rows.DerivedBySess.TryGetValue(header.SessionId, out List<StoredSessionContextDerivedCount>? der) ? der : [],
                    rows.FlaggedBySess.TryGetValue(header.SessionId, out List<int>? flg) ? flg : [],
                    ctxTurns);
            }

            StoredSessionSchema? schema = null;
            if (rows.SchemaHeadersBySess.TryGetValue(header.SessionId, out (int InvocationCount, int TurnCount, long? Unused) schHeader))
            {
                schema = new StoredSessionSchema(
                    schHeader.InvocationCount,
                    schHeader.TurnCount,
                    rows.RankedToolsBySess.TryGetValue(header.SessionId, out List<StoredSchemaRankedTool>? rtList) ? rtList : [],
                    rows.SchemaServersBySess.TryGetValue(header.SessionId, out List<StoredSchemaServerTokens>? ssList) ? ssList : [],
                    schHeader.Unused);
            }

            IReadOnlyList<string>? timelineSubagents = rows.SubagentsBySess.TryGetValue(header.SessionId, out List<string>? subList) && subList.Count > 0
                ? subList
                : null;
            IReadOnlyList<string>? timelineAuxiliaries = rows.AuxiliariesBySess.TryGetValue(header.SessionId, out List<string>? auxList) && auxList.Count > 0
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
                rows.RequestsBySess.TryGetValue(header.SessionId, out List<string>? reqList) ? reqList : [],
                rows.TurnsBySess.TryGetValue(header.SessionId, out List<StoredSessionTurn>? turnList) ? turnList : [],
                context,
                header.ContextUnmeasurableReason,
                schema,
                header.SchemaUnmeasurableReason,
                timelineSubagents,
                timelineAuxiliaries));
        }

        return projections;
    }

    private protected sealed record SessionProjectionRowSets(
        Dictionary<string, List<string>> RequestsBySess,
        Dictionary<string, List<StoredSessionTurn>> TurnsBySess,
        Dictionary<string, RawContextHeader> CtxHeadersBySess,
        Dictionary<string, List<string>> ObsBucketsBySess,
        Dictionary<string, List<StoredSessionContextDerivedCount>> DerivedBySess,
        Dictionary<string, List<int>> FlaggedBySess,
        Dictionary<string, List<RawContextTurnHeader>> CtxTurnsBySess,
        Dictionary<(string SessionId, int Position), List<StoredTurnBucketTokens>> CtxBucketsByTurn,
        Dictionary<(string SessionId, int Position), List<StoredTurnServerDefinitionTokens>> CtxServersByTurn,
        Dictionary<(string SessionId, int Position), List<TokenizerLabel>> CtxTokenizersByTurn,
        Dictionary<string, (int InvocationCount, int TurnCount, long? Unused)> SchemaHeadersBySess,
        Dictionary<string, List<StoredSchemaRankedTool>> RankedToolsBySess,
        Dictionary<string, List<StoredSchemaServerTokens>> SchemaServersBySess,
        Dictionary<string, List<string>> SubagentsBySess,
        Dictionary<string, List<string>> AuxiliariesBySess);

    private protected IReadOnlyList<StoredRun> QueryRuns(string? filterPredicate, Dictionary<string, object?> parameters)
    {
        string whereClause = filterPredicate is null ? string.Empty : $" WHERE {filterPredicate}";
        string sql =
            "SELECT r.run_id, r.harness_id, r.grouping_basis, r.grouping_rule, r.execution_count, " +
            "rl.label, rw.working_directory, rb.started_at_utc, rb.ended_at_utc " +
            "FROM run r " +
            "LEFT JOIN run_label rl ON rl.run_id = r.run_id " +
            "LEFT JOIN run_working_directory rw ON rw.run_id = r.run_id " +
            $"LEFT JOIN run_bounds rb ON rb.run_id = r.run_id{whereClause} ORDER BY r.rowid ASC";
        return QueryRows<StoredRun>(sql, parameters, static reader => new StoredRun(
            reader.GetString(reader.GetOrdinal("run_id")),
            reader.GetString(reader.GetOrdinal("harness_id")),
            reader.GetString(reader.GetOrdinal("grouping_basis")),
            reader.GetString(reader.GetOrdinal("grouping_rule")),
            reader.GetInt32(reader.GetOrdinal("execution_count")),
            ReadNullableString(reader, "label"),
            ReadNullableString(reader, "working_directory"),
            ReadNullableString(reader, "started_at_utc"),
            ReadNullableString(reader, "ended_at_utc")));
    }

    private protected IReadOnlyList<StoredExecution> QueryExecutions(string? filterPredicate, Dictionary<string, object?> parameters)
    {
        string whereClause = filterPredicate is null ? string.Empty : $" WHERE {filterPredicate}";
        string sql =
            "SELECT e.execution_id, e.run_id, e.harness_id, e.is_root, " +
            "es.session_id, ep.parent_execution_id, ea.agent_name, eb.started_at_utc, eb.ended_at_utc " +
            "FROM execution e " +
            "LEFT JOIN execution_session es ON es.execution_id = e.execution_id " +
            "LEFT JOIN execution_parent ep ON ep.execution_id = e.execution_id " +
            "LEFT JOIN execution_agent ea ON ea.execution_id = e.execution_id " +
            $"LEFT JOIN execution_bounds eb ON eb.execution_id = e.execution_id{whereClause} ORDER BY e.rowid ASC";
        return QueryRows<StoredExecution>(sql, parameters, static reader => new StoredExecution(
            reader.GetString(reader.GetOrdinal("execution_id")),
            reader.GetString(reader.GetOrdinal("run_id")),
            reader.GetString(reader.GetOrdinal("harness_id")),
            reader.GetInt64(reader.GetOrdinal("is_root")) == 1L,
            ReadNullableString(reader, "session_id"),
            ReadNullableString(reader, "parent_execution_id"),
            ReadNullableString(reader, "agent_name"),
            ReadNullableString(reader, "started_at_utc"),
            ReadNullableString(reader, "ended_at_utc")));
    }

    private protected IReadOnlyList<StoredHarnessRollup> QueryHarnessRollups(string? filterPredicate, Dictionary<string, object?> parameters)
    {
        string whereClause = filterPredicate is null ? string.Empty : $" WHERE {filterPredicate}";
        List<(string HarnessId, int Sessions, int Runs, int Executions, int Unknown, int Total)> headers =
            [.. QueryRows<(string HarnessId, int Sessions, int Runs, int Executions, int Unknown, int Total)>(
                "SELECT harness_id, session_count, run_count, execution_count, unknown_window_sessions, window_sessions_total " +
                $"FROM harness_rollup{whereClause} ORDER BY harness_id ASC",
                parameters,
                static reader => (
                    reader.GetString(reader.GetOrdinal("harness_id")),
                    reader.GetInt32(reader.GetOrdinal("session_count")),
                    reader.GetInt32(reader.GetOrdinal("run_count")),
                    reader.GetInt32(reader.GetOrdinal("execution_count")),
                    reader.GetInt32(reader.GetOrdinal("unknown_window_sessions")),
                    reader.GetInt32(reader.GetOrdinal("window_sessions_total"))))];

        Dictionary<string, List<StoredHarnessRollupMetric>> metricsByHarness = new(StringComparer.Ordinal);
        foreach ((string HarnessId, StoredHarnessRollupMetric Metric) row in QueryRows<(string HarnessId, StoredHarnessRollupMetric Metric)>(
                     "SELECT harness_id, metric, value, basis " +
                     $"FROM harness_rollup_metric{whereClause} ORDER BY harness_id ASC, rowid ASC",
                     parameters,
                     static reader => (
                         reader.GetString(reader.GetOrdinal("harness_id")),
                         new StoredHarnessRollupMetric(
                             reader.GetString(reader.GetOrdinal("metric")),
                             reader.GetDouble(reader.GetOrdinal("value")),
                             reader.GetString(reader.GetOrdinal("basis"))))))
        {
            GetOrCreateList(metricsByHarness, row.HarnessId).Add(row.Metric);
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

    private protected IReadOnlyList<StoredFinding> QueryFindings(string? filterPredicate, Dictionary<string, object?> parameters)
    {
        string whereClause = filterPredicate is null ? string.Empty : $" WHERE {filterPredicate}";
        string linkFilter = filterPredicate is null
            ? string.Empty
            : " WHERE fel.finding_id IN (SELECT f.finding_id FROM finding f LEFT JOIN finding_run fr ON fr.finding_id = f.finding_id LEFT JOIN finding_session fs ON fs.finding_id = f.finding_id WHERE " + filterPredicate + ")";

        string findSql =
            "SELECT f.finding_id, f.detector_id, f.title, f.mechanism, f.confidence, f.recommendation, " +
            "f.outcome_risk_caveat, f.rank_score, few.tokens, feb.lower, feb.upper, fr.run_id, fs.session_id, " +
            "fmc.measurement_class, fcb.confidence_basis, fww.text, fch.context_limit, fch.context_limit_source " +
            "FROM finding f " +
            "LEFT JOIN finding_estimated_waste few ON few.finding_id = f.finding_id " +
            "LEFT JOIN finding_error_bar feb ON feb.finding_id = f.finding_id " +
            "LEFT JOIN finding_run fr ON fr.finding_id = f.finding_id " +
            "LEFT JOIN finding_session fs ON fs.finding_id = f.finding_id " +
            "LEFT JOIN finding_measurement_class fmc ON fmc.finding_id = f.finding_id " +
            "LEFT JOIN finding_confidence_basis fcb ON fcb.finding_id = f.finding_id " +
            "LEFT JOIN finding_what_would_raise_it fww ON fww.finding_id = f.finding_id " +
            $"LEFT JOIN finding_compaction_hazard fch ON fch.finding_id = f.finding_id{whereClause} " +
            "ORDER BY f.rank_score DESC, f.rowid ASC";
        List<RawFindingHeader> headers = [.. QueryRows<RawFindingHeader>(
            findSql,
            parameters,
            static reader =>
            {
                double? lower = ReadNullableDouble(reader, "lower");
                double? upper = ReadNullableDouble(reader, "upper");
                FindingErrorBar? errorBar = lower.HasValue && upper.HasValue
                    ? new FindingErrorBar((long)lower.Value, (long)upper.Value)
                    : lower.HasValue || upper.HasValue
                        ? throw new StoredDataException("Partial finding_error_bar row returned.")
                        : null;

                long? hazardLimit = ReadNullableInt64(reader, "context_limit");
                string? hazardSource = ReadNullableString(reader, "context_limit_source");
                StoredCompactionHazard? hazard = hazardLimit.HasValue && hazardSource is not null
                    ? new StoredCompactionHazard(hazardLimit.Value, hazardSource)
                    : hazardLimit.HasValue || hazardSource is not null
                        ? throw new StoredDataException("Partial finding_compaction_hazard row returned.")
                        : null;

                return new RawFindingHeader(
                    reader.GetString(reader.GetOrdinal("finding_id")),
                    reader.GetString(reader.GetOrdinal("detector_id")),
                    reader.GetString(reader.GetOrdinal("title")),
                    reader.GetString(reader.GetOrdinal("mechanism")),
                    reader.GetString(reader.GetOrdinal("confidence")),
                    reader.GetString(reader.GetOrdinal("recommendation")),
                    reader.GetString(reader.GetOrdinal("outcome_risk_caveat")),
                    reader.GetDouble(reader.GetOrdinal("rank_score")),
                    ReadNullableInt64(reader, "tokens"),
                    errorBar,
                    ReadNullableString(reader, "run_id"),
                    ReadNullableString(reader, "session_id"),
                    ReadNullableString(reader, "measurement_class"),
                    ReadNullableString(reader, "confidence_basis"),
                    ReadNullableString(reader, "text"),
                    hazard);
            })];

        Dictionary<string, List<FindingEvidenceLink>> linksByFinding = new(StringComparer.Ordinal);
        foreach ((string FindingId, FindingEvidenceLink Link) row in QueryRows<(string FindingId, FindingEvidenceLink Link)>(
                     "SELECT fel.finding_id, fel.span_id, fel.turn_index, fel.description " +
                     $"FROM finding_evidence_link fel{linkFilter} ORDER BY fel.finding_id ASC, fel.position ASC",
                     parameters,
                     static reader => (
                         reader.GetString(reader.GetOrdinal("finding_id")),
                         new FindingEvidenceLink(
                             reader.GetString(reader.GetOrdinal("span_id")),
                             reader.GetInt32(reader.GetOrdinal("turn_index")),
                             reader.GetString(reader.GetOrdinal("description"))))))
        {
            GetOrCreateList(linksByFinding, row.FindingId).Add(row.Link);
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

    private protected IReadOnlyList<StoredPrediction> QueryPredictions(string? filterPredicate, Dictionary<string, object?> parameters)
    {
        string whereClause = filterPredicate is null ? string.Empty : $" WHERE {filterPredicate}";
        string sql =
            "SELECT p.prediction_id, p.finding_id, p.run_id, p.predicted_waste_tokens, p.confidence, " +
            "p.confidence_tier, p.created_at_utc, pc.comparison_run_id, pod.tokens, pcal.score " +
            "FROM prediction p " +
            "LEFT JOIN prediction_comparison pc ON pc.prediction_id = p.prediction_id " +
            "LEFT JOIN prediction_observed_delta pod ON pod.prediction_id = p.prediction_id " +
            $"LEFT JOIN prediction_calibration pcal ON pcal.prediction_id = p.prediction_id{whereClause} " +
            "ORDER BY p.rowid ASC";
        return QueryRows<StoredPrediction>(sql, parameters, static reader => new StoredPrediction(
            reader.GetString(reader.GetOrdinal("prediction_id")),
            reader.GetString(reader.GetOrdinal("finding_id")),
            reader.GetString(reader.GetOrdinal("run_id")),
            reader.GetInt64(reader.GetOrdinal("predicted_waste_tokens")),
            reader.GetDouble(reader.GetOrdinal("confidence")),
            reader.GetString(reader.GetOrdinal("confidence_tier")),
            reader.GetString(reader.GetOrdinal("created_at_utc")),
            ReadNullableString(reader, "comparison_run_id"),
            ReadNullableInt64(reader, "tokens"),
            ReadNullableDouble(reader, "score")));
    }

    private protected static string BuildSpanFilterWhere(string alias, string? spanId, string? sessionKey, Dictionary<string, object?> parameters)
    {
        List<string> predicates = [];
        if (spanId is not null)
        {
            predicates.Add($"{alias}.span_id = {Bind(parameters, spanId)}");
        }

        if (sessionKey is not null)
        {
            predicates.Add($"EXISTS (SELECT 1 FROM record_session_link rsl WHERE rsl.span_id = {alias}.span_id AND rsl.session_key = {Bind(parameters, sessionKey)})");
        }

        return predicates.Count > 0 ? " WHERE " + string.Join(" AND ", predicates) : string.Empty;
    }

    private protected static string BuildContentPartSelect(string alias, string whereClause, string orderBy) =>
        $"SELECT {alias}.span_id, {BuildContentPartColumns(alias)} " +
        BuildContentPartFrom(alias) +
        $"{whereClause} {orderBy}";

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
    private protected static string BuildBudgetedContentPartSelect(string alias, string whereClause, int budget) =>
        "SELECT * FROM (" +
        $"SELECT {alias}.span_id AS span_id, {BuildContentPartColumns(alias)}, " +
        "COALESCE(SUM(COALESCE(length(CAST(cpt.part_text AS BLOB)), 0)) OVER (" +
        $"ORDER BY {alias}.span_id ASC, {alias}.position ASC " +
        "ROWS BETWEEN UNBOUNDED PRECEDING AND 1 PRECEDING), 0) AS bytes_before " +
        BuildContentPartFrom(alias) +
        $"{whereClause}) WHERE bytes_before < {budget.ToString(CultureInfo.InvariantCulture)} " +
        "ORDER BY span_id ASC, position ASC";

    private protected static string BuildContentPartFrom(string alias) =>
        $"FROM content_part {alias} " +
        $"LEFT JOIN content_part_text cpt ON cpt.span_id = {alias}.span_id AND cpt.position = {alias}.position " +
        $"LEFT JOIN content_part_withheld cpw ON cpw.span_id = {alias}.span_id AND cpw.position = {alias}.position " +
        $"LEFT JOIN content_part_withheld_tokenizer cpwt ON cpwt.span_id = {alias}.span_id AND cpwt.position = {alias}.position " +
        $"LEFT JOIN content_part_reported_tokens cprt ON cprt.span_id = {alias}.span_id AND cprt.position = {alias}.position " +
        $"LEFT JOIN content_part_server cps ON cps.span_id = {alias}.span_id AND cps.position = {alias}.position " +
        $"LEFT JOIN content_part_role cpr ON cpr.span_id = {alias}.span_id AND cpr.position = {alias}.position " +
        $"LEFT JOIN content_part_residency cpres ON cpres.span_id = {alias}.span_id AND cpres.position = {alias}.position " +
        $"LEFT JOIN content_part_truncation cptr ON cptr.span_id = {alias}.span_id AND cptr.position = {alias}.position";

    private protected static string BuildContentPartColumns(string alias) =>
        $"{alias}.position, {alias}.bucket, " +
        "cpt.part_text, " +
        "cpw.utf8_bytes, " +
        "cpw.characters, " +
        "cpw.measured_tokens, " +
        "cprt.token_count, " +
        "cps.server_name, " +
        "cpr.role_name, " +
        "cpres.is_resident, " +
        "cptr.original_utf8_bytes, " +
        "cpwt.tokenizer, " +
        "cpwt.is_model_own";

    private protected static CollectedContentPart ReadContentPartColumns(SqliteDataReader reader)
    {
        int position = reader.GetInt32(reader.GetOrdinal("position"));
        ContentBucket bucket = ParseContentBucket(reader.GetString(reader.GetOrdinal("bucket")));
        string? plainText = ReadNullableString(reader, "part_text");
        long? wBytes = ReadNullableInt64(reader, "utf8_bytes");
        long? wChars = ReadNullableInt64(reader, "characters");
        long? wTokens = ReadNullableInt64(reader, "measured_tokens");
        string? wTokenizer = ReadNullableString(reader, "tokenizer");
        long? wModelOwnRaw = ReadNullableInt64(reader, "is_model_own");
        bool wModelOwn = wModelOwnRaw switch
        {
            null => false,
            0L => false,
            1L => true,
            _ => throw new StoredDataException($"Invalid boolean flag '{wModelOwnRaw.Value.ToString(CultureInfo.InvariantCulture)}' in content_part_withheld_tokenizer.is_model_own."),
        };
        if ((wTokenizer is null) != (wModelOwnRaw is null))
        {
            throw new StoredDataException("Partial content_part_withheld_tokenizer row returned.");
        }

        CollectedWithheldText? withheld = wBytes.HasValue && wChars.HasValue && wTokens.HasValue
            ? new CollectedWithheldText(wBytes.Value, wChars.Value, wTokens.Value, wTokenizer, wModelOwn)
            : wBytes.HasValue || wChars.HasValue || wTokens.HasValue
                ? throw new StoredDataException("Partial content_part_withheld row returned.")
                : null;
        long? reportedTokens = ReadNullableInt64(reader, "token_count");
        string? server = ReadNullableString(reader, "server_name");
        string? role = ReadNullableString(reader, "role_name");
        long? residentRaw = ReadNullableInt64(reader, "is_resident");
        bool? resident = residentRaw.HasValue
            ? residentRaw.Value switch
            {
                0L => false,
                1L => true,
                _ => throw new StoredDataException($"Invalid boolean flag '{residentRaw.Value.ToString(CultureInfo.InvariantCulture)}' in content_part_residency.is_resident."),
            }
            : null;
        long? originalBytes = ReadNullableInt64(reader, "original_utf8_bytes");

        return new CollectedContentPart(position, bucket, plainText, withheld, reportedTokens, server, role, resident, originalBytes);
    }

    private protected static string BuildToolCallSelect(string alias, string whereClause, string orderBy) =>
        $"SELECT {alias}.span_id, {BuildToolCallColumns(alias)} " +
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
        $"{whereClause} {orderBy}";

    private protected static string BuildToolCallColumns(string alias) =>
        $"{alias}.call_id, {alias}.tool_name, " +
        "tcm.server_name, " +
        "tcm.server_tool_name, " +
        "tcis.utf8_bytes AS input_utf8_bytes, " +
        "tcis.characters AS input_characters, " +
        "tcit.text AS input_text, " +
        "tcif.sha256 AS input_sha256, " +
        "tcitrc.original_utf8_bytes AS input_original_utf8_bytes, " +
        "tcrs.utf8_bytes AS result_utf8_bytes, " +
        "tcrs.characters AS result_characters, " +
        "tcrt.text AS result_text, " +
        "tcrtrc.original_utf8_bytes AS result_original_utf8_bytes, " +
        "tco.succeeded, " +
        "tce.message AS error_message, " +
        "tcec.code AS error_code, " +
        "tcex.exit_code";

    /// <summary>
    /// Tool-call columns without the two content-bearing text expressions, in the same shape as
    /// <see cref="BuildToolCallColumns"/> so both parses share one reader.
    /// </summary>
    private protected static string BuildToolCallMetadataColumns(string alias) =>
        $"{alias}.call_id, {alias}.tool_name, " +
        "tcm.server_name, " +
        "tcm.server_tool_name, " +
        "tcis.utf8_bytes AS input_utf8_bytes, " +
        "tcis.characters AS input_characters, " +
        "NULL AS input_text, " +
        "tcif.sha256 AS input_sha256, " +
        "tcitrc.original_utf8_bytes AS input_original_utf8_bytes, " +
        "tcrs.utf8_bytes AS result_utf8_bytes, " +
        "tcrs.characters AS result_characters, " +
        "NULL AS result_text, " +
        "tcrtrc.original_utf8_bytes AS result_original_utf8_bytes, " +
        "tco.succeeded, " +
        "tce.message AS error_message, " +
        "tcec.code AS error_code, " +
        "tcex.exit_code";

    private protected static CollectedToolCall ReadToolCallColumns(SqliteDataReader reader)
    {
        string callId = reader.GetString(reader.GetOrdinal("call_id"));
        string toolName = reader.GetString(reader.GetOrdinal("tool_name"));
        string? mcpServer = ReadNullableString(reader, "server_name");
        string? mcpTool = ReadNullableString(reader, "server_tool_name");

        long? inBytes = ReadNullableInt64(reader, "input_utf8_bytes");
        long? inChars = ReadNullableInt64(reader, "input_characters");
        string? inText = ReadNullableString(reader, "input_text");
        string? inSha = ReadNullableString(reader, "input_sha256");
        long? inTrunc = ReadNullableInt64(reader, "input_original_utf8_bytes");
        CollectedToolContent? input = inBytes.HasValue && inChars.HasValue
            ? new CollectedToolContent(inText, inBytes.Value, inChars.Value, inSha, inTrunc)
            : null;

        long? resBytes = ReadNullableInt64(reader, "result_utf8_bytes");
        long? resChars = ReadNullableInt64(reader, "result_characters");
        string? resText = ReadNullableString(reader, "result_text");
        long? resTrunc = ReadNullableInt64(reader, "result_original_utf8_bytes");
        CollectedToolContent? result = resBytes.HasValue && resChars.HasValue
            ? new CollectedToolContent(resText, resBytes.Value, resChars.Value, null, resTrunc)
            : null;

        long? succeededRaw = ReadNullableInt64(reader, "succeeded");
        bool? succeeded = succeededRaw.HasValue
            ? succeededRaw.Value switch
            {
                0L => false,
                1L => true,
                _ => throw new StoredDataException($"Invalid boolean flag '{succeededRaw.Value.ToString(CultureInfo.InvariantCulture)}' in tool_call_outcome.succeeded."),
            }
            : null;

        string? errorMsg = ReadNullableString(reader, "error_message");
        string? errorCode = ReadNullableString(reader, "error_code");
        CollectedError? error = errorMsg is not null ? new CollectedError(errorMsg, errorCode) : null;

        long? exitCode = ReadNullableInt64(reader, "exit_code");
        return new CollectedToolCall(callId, toolName, mcpServer, mcpTool, input, result, succeeded, error, exitCode);
    }

    private protected static string BuildAgentInvocationSelect(string alias, string whereClause, string orderBy) =>
        $"SELECT {alias}.span_id, {BuildAgentInvocationColumns(alias)} " +
        $"FROM agent_invocation {alias} " +
        $"LEFT JOIN agent_invocation_spawn ais ON ais.span_id = {alias}.span_id " +
        $"LEFT JOIN agent_invocation_outcome aio ON aio.span_id = {alias}.span_id " +
        $"LEFT JOIN agent_invocation_error aie ON aie.span_id = {alias}.span_id" +
        $"{whereClause} {orderBy}";

    private protected static string BuildAgentInvocationColumns(string alias) =>
        $"{alias}.agent_name, " +
        "ais.spawning_record_id, " +
        "aio.succeeded, " +
        "aie.message AS error_message";

    private protected static CollectedAgentInvocation ReadAgentInvocationColumns(SqliteDataReader reader)
    {
        string agentName = reader.GetString(reader.GetOrdinal("agent_name"));
        string? spawnId = ReadNullableString(reader, "spawning_record_id");
        long? succeededRaw = ReadNullableInt64(reader, "succeeded");
        bool? succeeded = succeededRaw.HasValue
            ? succeededRaw.Value switch
            {
                0L => false,
                1L => true,
                _ => throw new StoredDataException($"Invalid boolean flag '{succeededRaw.Value.ToString(CultureInfo.InvariantCulture)}' in agent_invocation_outcome.succeeded."),
            }
            : null;
        string? error = ReadNullableString(reader, "error_message");
        return new CollectedAgentInvocation(agentName, spawnId, succeeded, error);
    }

    private protected static string? BuildStoredFieldsScopeWhere(TableDefinition table, string sessionExpr)
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

    /// <summary>Reads one row, in catalog column order, from the raw column values.</summary>
    private protected static StoredFieldRow ReadStoredFieldRow(SqliteDataReader reader, TableDefinition table)
    {
        List<StoredFieldCell> cells = new(table.Columns.Count);
        for (int colIndex = 0; colIndex < table.Columns.Count; colIndex++)
        {
            cells.Add(ReadStoredFieldCell(reader, table.Columns[colIndex]));
        }

        return new StoredFieldRow(cells);
    }

    private protected static StoredFieldCell ReadStoredFieldCell(SqliteDataReader reader, ColumnDefinition column)
    {
        int ordinal = reader.GetOrdinal(column.Name);
        if (reader.IsDBNull(ordinal))
        {
            return new StoredFieldCell(column.Name, column.Type);
        }

        object value = reader.GetValue(ordinal);
        string site = column.Name;
        return column.Type switch
        {
            SqliteColumnType.SqliteText => value is string text
                ? new StoredFieldCell(column.Name, column.Type, TextValue: text)
                : throw new StoredDataException($"Unexpected SQLite storage type for text column '{site}'."),
            SqliteColumnType.SqliteInteger => value is long integer
                ? new StoredFieldCell(column.Name, column.Type, IntegerValue: integer)
                : throw new StoredDataException($"Unexpected SQLite storage type for integer column '{site}'."),
            SqliteColumnType.SqliteReal => value switch
            {
                long integer => new StoredFieldCell(column.Name, column.Type, RealValue: integer),
                double real => new StoredFieldCell(column.Name, column.Type, RealValue: real),
                _ => throw new StoredDataException($"Unexpected SQLite storage type for real column '{site}'."),
            },
            _ => throw new StoredDataException($"Unsupported column type '{column.Type}' on '{site}'."),
        };
    }

    /// <summary>
    /// The key columns each table's missing-parent-key computation reads: a qualifying child (one
    /// foreign key whose columns are its primary key) reads its own key columns, and the parent
    /// such a child references reads the referenced columns. Every other column stays unread.
    /// </summary>
    private protected static Dictionary<string, List<ColumnDefinition>> StoredFieldsKeyColumns(IReadOnlyList<TableDefinition> tables)
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

    private protected static List<string> ComputeStoredFieldsMissingParentKeys(
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

    private protected static int[] ResolveColumnIndices(TableDefinition table, IReadOnlyList<string> columnNames)
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

    private protected static string FormatStoredRowKey(StoredFieldRow row, int[] keyIndices)
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

    private protected static string FormatStoredCellValue(StoredFieldCell cell) => cell.ColumnType switch
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
}

/// <summary>
/// Session figure reads over a SQLite relational store.
/// </summary>
public abstract class SqliteRelationalStoreReadCore : SqliteRelationalStoreReadQueryBase
{
    /// <summary>Initializes the figure reads over <paramref name="databasePath"/>.</summary>
    /// <param name="databasePath">Path to the SQLite database file.</param>
    /// <param name="circuitBreaker">Circuit breaker guarding every <c>sqlite3</c> invocation.</param>
    /// <param name="fileSystem">Filesystem seam; defaults to <see cref="PhysicalRelationalStoreFileSystem.Instance"/> when <see langword="null"/>.</param>
    protected SqliteRelationalStoreReadCore(
        string databasePath,
        ICircuitBreaker circuitBreaker,
        IRelationalStoreFileSystem? fileSystem)
        : base(databasePath, circuitBreaker, fileSystem)
    {
    }

    private protected static IReadOnlyList<ReadTimeTurnSummary> BuildContentFreeTurnSummaries(
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
    private protected static SessionFigureSet BuildSessionFigures(
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
        SessionTurnAccumulation turns = AccumulateSessionTurns(turnEntries, harnessId, pricing);

        List<CollectedRecord> rootRecords = ResolveSessionRootRecords(records, sessionSpanIds);
        bool hasNesting = records.Any(r =>
            r.ParentRecordId is not null &&
            !string.Equals(r.ParentRecordId, r.RecordId, StringComparison.Ordinal) &&
            sessionSpanIds.Contains(r.ParentRecordId));

        IReadOnlyList<CollectedRecord> durationCandidates = hasNesting ? rootRecords : records;
        (long totalDuration, bool hasDuration) = ComputeSessionDuration(durationCandidates);

        List<Problem> extraProblems = [];
        ReadTimeCostSummary? sessionReportedCost = ResolveSessionReportedCost(records, extraProblems);

        // A session whose records carry no tokens is priced from the usage the harness reported
        // for the whole session, per model; records with tokens keep pricing per turn.
        ReportedUsageCost? reportedUsageCost = ReportedSessionUsage.Price(session, records, pricing);
        if (reportedUsageCost is not null)
        {
            extraProblems.AddRange(reportedUsageCost.Problems);
        }

        ReadTimeCostSummary sessionPublishedCost = ResolveSessionPublishedCost(
            reportedUsageCost,
            turns.TurnPublishedBlocks,
            extraProblems);
        ReadTimeCostBasisMismatch? basisMismatch = ResolveSessionCostBasisMismatch(
            turns.ValuedTurnHarnessBlocks,
            turns.ValuedTurnPublishedBlocks,
            extraProblems);

        long? nullableTotalInput = turns.TotalInput;
        long? nullableTotalOutput = turns.TotalOutput;
        long? nullableTotalCacheRead = turns.TotalCacheRead;
        long? nullableTotalCacheCreation = turns.TotalCacheCreation;

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
            ? turns.CacheCreationTurns
            : null;

        // Tool figures come from the metadata tables the content-free record read carries:
        // tool names, servers, definition schema sizes, and outcomes are metadata, while the
        // tool input and result text tables are never selected.
        SessionToolAggregation toolAggregation = AggregateSessionTools(records);
        int? toolCalls = toolAggregation.ToolCallTotal > 0 ? toolAggregation.ToolCallTotal : null;

        List<ReadTimeSessionToolSummary> tools = BuildReaderToolSummaries(
            projection,
            turnEntries.Count,
            toolAggregation.DefStats,
            toolAggregation.ToolsInvoked,
            toolAggregation.InvCounts,
            toolAggregation.ErrCounts);
        (int reconciliationTotal, int reconciliationMismatched) = CountReconciliation(turns.Reconciliation);

        IReadOnlyList<string> requestSpanIds = projection is not null && projection.RequestSpanIds.Count > 0
            ? projection.RequestSpanIds
            : [.. rootRecords.Select(static r => r.RecordId)];
        int requestCount = requestSpanIds.Count;
        List<ReadTimeSessionRequestSummary> requests = BuildReaderRequestSummaries(
            requestSpanIds,
            records,
            recordsBySpanId,
            turnEntries);

        // Server rows, context buckets, and the unused-schema range all price the stored
        // projection and the content-free tool metadata above, so no content column is read.
        List<ReadTimeSessionServerSummary> servers = BuildReaderServerSummaries(projection, tools);
        (ReadTimeContextBucketSummary? firstContextBucket, ReadTimeContextBucketSummary? lastContextBucket) =
            BuildSessionContextBuckets(projection, turnEntries);
        UnusedSchemaRange? unusedRange = ResolveReaderUnusedRange(projection, harnessId, turns.Models, pricing);

        List<Problem> problems = [.. storedProblems, .. extraProblems];

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
                Models: turns.Models,
                CacheHitRatio: cacheHitRatio,
                CacheCreationCoverage: cacheCreationCoverage,
                ToolCalls: toolCalls,
                ToolsInvoked: toolAggregation.ToolsInvoked,
                HasToolDefinitions: toolAggregation.ToolDefinitionRowCount > 0,
                ToolsOffered: toolAggregation.ToolsOffered,
                ReportedCost: sessionReportedCost,
                PublishedCost: sessionPublishedCost,
                CostBasisMismatch: basisMismatch,
                Coverage: BuildSessionCoverage(projection),
                Problems: problems,
                SkillInvocations: [],
                ToolRowsCount: tools.Count,
                UnmeasuredInputTurns: turns.UnmeasuredInputTurns,
                ReconciliationTotal: reconciliationTotal,
                ReconciliationMismatched: reconciliationMismatched,
                ReportedTotalTokens: reportedUsageTotals?.Total),
            tools,
            requests,
            servers,
            turns.Reconciliation,
            firstContextBucket,
            lastContextBucket,
            unusedRange);
    }

    private protected static SessionTurnAccumulation AccumulateSessionTurns(
        IReadOnlyList<(int Position, CollectedRecord Record)> turnEntries,
        string harnessId,
        IModelPricingLookup? pricing)
    {
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

        return new SessionTurnAccumulation(
            turnPublishedBlocks,
            valuedTurnPublishedBlocks,
            valuedTurnHarnessBlocks,
            models,
            hasInput ? totalInput : null,
            hasOutput ? totalOutput : null,
            hasCacheRead ? totalCacheRead : null,
            hasCacheCreation ? totalCacheCreation : null,
            cacheCreationTurns,
            reconciliation,
            unmeasuredInputTurns);
    }

    private protected static List<CollectedRecord> ResolveSessionRootRecords(
        IReadOnlyList<CollectedRecord> records,
        HashSet<string> sessionSpanIds)
    {
        return
        [
            .. records
                .Where(r => r.ParentRecordId is null ||
                            string.Equals(r.ParentRecordId, r.RecordId, StringComparison.Ordinal) ||
                            !sessionSpanIds.Contains(r.ParentRecordId))
                .OrderBy(static r => r.StartedAt)
                .ThenBy(static r => r.RecordId, StringComparer.Ordinal),
        ];
    }

    private protected static (long TotalDuration, bool HasDuration) ComputeSessionDuration(IReadOnlyList<CollectedRecord> durationCandidates)
    {
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

        return (totalDuration, hasDuration);
    }

    private protected static List<CostBlock> BuildReportedRecordBlocks(IReadOnlyList<CollectedRecord> records)
    {
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

        return reportedRecordBlocks;
    }

    private protected static ReadTimeCostSummary? ResolveSessionReportedCost(
        IReadOnlyList<CollectedRecord> records,
        List<Problem> extraProblems)
    {
        List<CostBlock> reportedRecordBlocks = BuildReportedRecordBlocks(records);
        return reportedRecordBlocks.Count > 0
            ? AggregateReaderCostBlocks(reportedRecordBlocks, CostBasis.Harness, extraProblems)
            : null;
    }

    private protected static ReadTimeCostSummary ResolveSessionPublishedCost(
        ReportedUsageCost? reportedUsageCost,
        List<CostBlock> turnPublishedBlocks,
        List<Problem> extraProblems)
    {
        if (reportedUsageCost is not null)
        {
            return ToReaderCostSummary(reportedUsageCost.Cost);
        }

        return turnPublishedBlocks.Count > 0
            ? AggregateReaderCostBlocks(turnPublishedBlocks, CostBasis.Published, extraProblems)
            : new ReadTimeCostSummary(CostBasis.Unknown, CostStatus.NoRate, null, null, []);
    }

    private protected static ReadTimeCostBasisMismatch? ResolveSessionCostBasisMismatch(
        List<CostBlock> valuedTurnHarnessBlocks,
        List<CostBlock> valuedTurnPublishedBlocks,
        List<Problem> extraProblems)
    {
        if (valuedTurnHarnessBlocks.Count == 0 || valuedTurnPublishedBlocks.Count == 0)
        {
            return null;
        }

        List<CostBlock> combined = [.. valuedTurnHarnessBlocks, .. valuedTurnPublishedBlocks];
        CostTotalResult mismatchTotal = CanonPricing.SumCosts(combined);
        CostBasisMismatch? domainMismatch = CanonPricing.CostBasisMismatchOf(combined, mismatchTotal);
        ReadTimeCostBasisMismatch? basisMismatch = null;
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

        return basisMismatch;
    }

    private protected static SessionToolAggregation AggregateSessionTools(IReadOnlyList<CollectedRecord> records)
    {
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

        return new SessionToolAggregation(
            toolCallTotal,
            toolsInvoked,
            invCounts,
            errCounts,
            toolsOffered,
            defStats,
            toolDefinitionRowCount);
    }

    private protected static (int Total, int Mismatched) CountReconciliation(
        IReadOnlyList<ReadTimeTurnReconciliation> reconciliation)
    {
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

        return (reconciliationTotal, reconciliationMismatched);
    }

    private protected static ReadTimeSessionCoverage BuildSessionCoverage(StoredSessionProjection? projection) =>
        new(
            SchemaMeasurable: projection?.Schema is not null,
            ContextMeasurable: projection?.Context is not null);

    private protected static (ReadTimeContextBucketSummary? First, ReadTimeContextBucketSummary? Last) BuildSessionContextBuckets(
        StoredSessionProjection? projection,
        IReadOnlyList<(int Position, CollectedRecord Record)> turnEntries)
    {
        if (projection?.Context?.Turns is not { Count: > 0 } contextTurns)
        {
            return (null, null);
        }

        List<StoredSessionContextTurn> orderedContext = [.. contextTurns.OrderBy(static t => t.Position)];
        return (
            BuildReaderContextBucketSummary(orderedContext[0], turnEntries),
            BuildReaderContextBucketSummary(orderedContext[^1], turnEntries));
    }

    private protected sealed record SessionTurnAccumulation(
        List<CostBlock> TurnPublishedBlocks,
        List<CostBlock> ValuedTurnPublishedBlocks,
        List<CostBlock> ValuedTurnHarnessBlocks,
        List<string> Models,
        long? TotalInput,
        long? TotalOutput,
        long? TotalCacheRead,
        long? TotalCacheCreation,
        int CacheCreationTurns,
        List<ReadTimeTurnReconciliation> Reconciliation,
        int UnmeasuredInputTurns);

    private protected sealed record SessionToolAggregation(
        int ToolCallTotal,
        List<string> ToolsInvoked,
        Dictionary<string, int> InvCounts,
        Dictionary<string, int> ErrCounts,
        List<string> ToolsOffered,
        Dictionary<string, (long SchemaTokens, string? Server, HashSet<string> SpanIds)> DefStats,
        int ToolDefinitionRowCount);

    /// <summary>One session's content-free figures with the detail page's tool and request rows.</summary>
    private protected sealed record SessionFigureSet(
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
    private protected static SessionListFigure BuildSessionListFigure(
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
        SessionTurnAccumulation turns = AccumulateSessionTurns(turnEntries, harnessId, pricing);
        List<CollectedRecord> rootRecords = ResolveSessionRootRecords(records, sessionSpanIds);

        List<Problem> extraProblems = [];
        List<CostBlock> reportedRecordBlocks = BuildReportedRecordBlocks(records);
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

        ReadTimeCostSummary sessionPublishedCost = ResolveSessionPublishedCost(
            reportedUsageCost,
            turns.TurnPublishedBlocks,
            extraProblems);
        ReadTimeCostBasisMismatch? basisMismatch = ResolveSessionCostBasisMismatch(
            turns.ValuedTurnHarnessBlocks,
            turns.ValuedTurnPublishedBlocks,
            extraProblems);

        int requestCount = projectionRequestCount is > 0 ? projectionRequestCount.Value : rootRecords.Count;

        ReportedSessionUsageTotals? listUsageTotals = ReportedSessionUsage.Resolve(session, records);
        return new SessionListFigure(
            SessionKey: sessionKey,
            HarnessId: harnessId,
            Repo: session.Workspace?.Repository,
            Branch: session.Workspace?.Branch,
            TurnCount: turnEntries.Count,
            RequestCount: requestCount,
            TotalInput: listUsageTotals is not null ? listUsageTotals.CombinedInput : turns.TotalInput,
            TotalOutput: listUsageTotals is not null ? listUsageTotals.Output : turns.TotalOutput,
            ReportedCost: sessionReportedCost,
            PublishedCost: sessionPublishedCost,
            CostBasisMismatch: basisMismatch,
            Models: turns.Models,
            ProblemsCount: storedProblems.Count + extraProblems.Count,
            ReportedTotalTokens: listUsageTotals?.Total);
    }
}

/// <summary>
/// The read half of <see cref="SqliteRelationalStore"/>: schema reads, session projections, and
/// content-free figures over a SQLite relational store.
/// </summary>
/// <summary>
/// The read half of <see cref="SqliteRelationalStore"/>: schema reads, session projections, and
/// content-free figures over a SQLite relational store.
/// </summary>
public abstract class SqliteRelationalStoreReaderBase : SqliteRelationalStoreReadFigureBase, IRelationalStoreReader
{

    /// <summary>Initializes the read half over <paramref name="databasePath"/>.</summary>
    /// <param name="databasePath">Path to the SQLite database file.</param>
    /// <param name="circuitBreaker">Circuit breaker guarding every <c>sqlite3</c> invocation.</param>
    /// <param name="fileSystem">Filesystem seam; defaults to <see cref="PhysicalRelationalStoreFileSystem.Instance"/> when <see langword="null"/>.</param>
    protected SqliteRelationalStoreReaderBase(
        string databasePath,
        ICircuitBreaker circuitBreaker,
        IRelationalStoreFileSystem? fileSystem)
        : base(databasePath, circuitBreaker, fileSystem)
    {
    }

    /// <inheritdoc />
    public IReadOnlyList<string> Harnesses()
    {
        const string Sql = "SELECT harness_id FROM harness ORDER BY harness_id ASC";
        return QueryRows<string>(Sql, new Dictionary<string, object?>(StringComparer.Ordinal), static reader => reader.GetString(0));
    }

    /// <inheritdoc />
    public IReadOnlyList<StoredSource> Sources(string? harnessId = null)
    {
        Dictionary<string, object?> parameters = new(StringComparer.Ordinal);
        string sql = "SELECT source_id, harness_id, source_kind, locator FROM source";
        if (harnessId is not null)
        {
            sql += " WHERE harness_id = " + Bind(parameters, harnessId);
        }

        sql += " ORDER BY rowid ASC";
        return QueryRows<StoredSource>(sql, parameters, static reader => new StoredSource(
            reader.GetString(reader.GetOrdinal("source_id")),
            new CollectedSource(
                reader.GetString(reader.GetOrdinal("harness_id")),
                ParseSourceKind(reader.GetString(reader.GetOrdinal("source_kind"))),
                reader.GetString(reader.GetOrdinal("locator")))));
    }

    /// <inheritdoc />
    public int CountRecords(string? harnessId = null)
    {
        Dictionary<string, object?> parameters = new(StringComparer.Ordinal);
        string sql = "SELECT COUNT(*) AS record_count FROM record";
        if (harnessId is not null)
        {
            sql += " WHERE harness_id = " + Bind(parameters, harnessId);
        }

        IReadOnlyList<int> rows = QueryRows<int>(sql, parameters, static reader => reader.GetInt32(reader.GetOrdinal("record_count")));
        if (rows.Count != 1)
        {
            throw new StoredDataException($"Expected single scalar row from record count query, got {rows.Count}.");
        }

        return rows[0];
    }

    /// <inheritdoc />
    public IReadOnlyList<StoredSourceCount> SourceRecordCounts()
    {
        const string Sql =
            "SELECT source_id, COUNT(*) AS source_record_count FROM record " +
            "WHERE source_id IS NOT NULL GROUP BY source_id ORDER BY source_id ASC";
        return QueryRows<StoredSourceCount>(Sql, new Dictionary<string, object?>(StringComparer.Ordinal), static reader => new StoredSourceCount(
            reader.GetString(reader.GetOrdinal("source_id")),
            reader.GetInt32(reader.GetOrdinal("source_record_count"))));
    }

    /// <inheritdoc />
    public IReadOnlyList<string> SessionKeysWithRecordSource(string harnessId, string sourceName, int limit)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(harnessId);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceName);
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);

        // Grouped by session key so each key appears once, with its newest record timestamp
        // driving the order. Started-at values are written in one fixed ISO-8601 format, so the
        // textual MAX orders chronologically.
        Dictionary<string, object?> parameters = new(StringComparer.Ordinal);
        string harnessMarker = Bind(parameters, harnessId);
        string sourceMarker = Bind(parameters, sourceName);
        string limitMarker = Bind(parameters, limit);
        string sql =
            "SELECT rsl.session_key FROM record r " +
            "JOIN record_session_link rsl ON rsl.span_id = r.span_id " +
            "JOIN source src ON src.source_id = r.source_id " +
            $"WHERE src.harness_id = {harnessMarker} AND src.locator = {sourceMarker} " +
            "GROUP BY rsl.session_key " +
            "ORDER BY MAX(r.started_at_utc) DESC, rsl.session_key ASC " +
            $"LIMIT {limitMarker}";
        return QueryRows<string>(sql, parameters, static reader => reader.GetString(0));
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
        Dictionary<string, object?> parameters = new(StringComparer.Ordinal);
        string spanMarker = Bind(parameters, spanId);
        string sql =
            "SELECT cp.bucket, cpt.part_text " +
            "FROM content_part cp " +
            "LEFT JOIN content_part_text cpt ON cpt.span_id = cp.span_id AND cpt.position = cp.position " +
            $"WHERE cp.span_id = {spanMarker} ORDER BY cp.position ASC";

        foreach ((ContentBucket bucket, string? text) in QueryRows<(ContentBucket Bucket, string? Text)>(
                     sql,
                     parameters,
                     static reader => (
                         ParseContentBucket(reader.GetString(reader.GetOrdinal("bucket"))),
                         reader.IsDBNull(reader.GetOrdinal("part_text")) ? null : reader.GetString(reader.GetOrdinal("part_text")))))
        {
            if (bucket is not (ContentBucket.UserPrompt or ContentBucket.UserMessage or ContentBucket.ConversationHistory))
            {
                continue;
            }

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
        const string Sql =
            "SELECT rsl.session_key, r.harness_id, COUNT(*) AS attribution_count " +
            "FROM record r JOIN record_session_link rsl ON rsl.span_id = r.span_id " +
            "GROUP BY rsl.session_key, r.harness_id " +
            "ORDER BY rsl.session_key ASC, r.harness_id ASC";
        return QueryRows<StoredSessionAttribution>(Sql, new Dictionary<string, object?>(StringComparer.Ordinal), static reader => new StoredSessionAttribution(
            reader.GetString(reader.GetOrdinal("session_key")),
            reader.GetString(reader.GetOrdinal("harness_id")),
            reader.GetInt32(reader.GetOrdinal("attribution_count"))));
    }

    /// <inheritdoc />
    public IReadOnlyList<CollectedRecord> SessionRecordsForRebuild(string sessionKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionKey);
        Dictionary<string, object?> parameters = new(StringComparer.Ordinal);
        return QueryRecords(RecordSessionLinkPredicate(sessionKey, parameters), parameters);
    }

    /// <inheritdoc />
    public IReadOnlyList<CollectedRecord> AllSessionRecordsForMaintenance() =>
        QueryRecords(null, new Dictionary<string, object?>(StringComparer.Ordinal));

    /// <summary>Builds the <c>record_session_link</c> existence predicate selecting a session key's records.</summary>
    /// <param name="sessionKey">Canonical session key.</param>
    /// <param name="parameters">Parameter values the predicate binds.</param>
    private static string RecordSessionLinkPredicate(string sessionKey, Dictionary<string, object?> parameters)
    {
        string sessionMarker = Bind(parameters, sessionKey);
        return $"EXISTS (SELECT 1 FROM record_session_link rsl WHERE rsl.span_id = r.span_id AND rsl.session_key = {sessionMarker})";
    }

    /// <inheritdoc />
    public IReadOnlyList<StoredTokenCount> TokenCounts(string? spanId = null, string? sessionKey = null)
    {
        Dictionary<string, object?> parameters = new(StringComparer.Ordinal);
        string whereClause = BuildSpanFilterWhere("rtc", spanId, sessionKey, parameters);
        string sql =
            "SELECT rtc.span_id, rtc.token_class, rtc.token_count " +
            $"FROM record_token_count rtc{whereClause} ORDER BY rtc.rowid ASC";
        return QueryRows<StoredTokenCount>(sql, parameters, static reader => new StoredTokenCount(
            reader.GetString(reader.GetOrdinal("span_id")),
            ParseTokenClass(reader.GetString(reader.GetOrdinal("token_class"))),
            reader.GetInt64(reader.GetOrdinal("token_count"))));
    }

    /// <inheritdoc />
    public IReadOnlyList<StoredReportedTotal> ReportedTotals(string? spanId = null, string? sessionKey = null)
    {
        Dictionary<string, object?> parameters = new(StringComparer.Ordinal);
        string whereClause = BuildSpanFilterWhere("rrt", spanId, sessionKey, parameters);
        string sql =
            "SELECT rrt.span_id, rrt.direction, rrt.token_count " +
            $"FROM record_reported_total rrt{whereClause} ORDER BY rrt.rowid ASC";
        return QueryRows<StoredReportedTotal>(sql, parameters, static reader => new StoredReportedTotal(
            reader.GetString(reader.GetOrdinal("span_id")),
            ParseReportedDirection(reader.GetString(reader.GetOrdinal("direction"))),
            reader.GetInt64(reader.GetOrdinal("token_count"))));
    }

    /// <inheritdoc />
    public IReadOnlyList<StoredReportedCost> ReportedCosts(string? spanId = null, string? sessionKey = null)
    {
        Dictionary<string, object?> parameters = new(StringComparer.Ordinal);
        string whereClause = BuildSpanFilterWhere("rrc", spanId, sessionKey, parameters);
        string sql =
            "SELECT rrc.span_id, rrc.amount, rrc.currency " +
            $"FROM record_reported_cost rrc{whereClause} ORDER BY rrc.rowid ASC";
        return QueryRows<StoredReportedCost>(sql, parameters, static reader => new StoredReportedCost(
            reader.GetString(reader.GetOrdinal("span_id")),
            reader.GetDouble(reader.GetOrdinal("amount")),
            reader.GetString(reader.GetOrdinal("currency"))));
    }

    /// <inheritdoc />
    public IReadOnlyList<StoredToolCall> ToolCalls(string? spanId = null, string? sessionKey = null)
    {
        Dictionary<string, object?> parameters = new(StringComparer.Ordinal);
        string whereClause = BuildSpanFilterWhere("tc", spanId, sessionKey, parameters);
        string sql = BuildToolCallSelect("tc", whereClause, "ORDER BY tc.rowid ASC");
        return QueryRows<StoredToolCall>(sql, parameters, static reader => new StoredToolCall(
            reader.GetString(reader.GetOrdinal("span_id")),
            ReadToolCallColumns(reader)));
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

        Dictionary<string, object?> criteria = new(StringComparer.Ordinal);
        List<string> predicates =
        [
            $"EXISTS (SELECT 1 FROM record_session_link rsl WHERE rsl.span_id = cp.span_id AND rsl.session_key = {Bind(criteria, sessionKey)})",
        ];
        if (!string.IsNullOrEmpty(scopedSpan))
        {
            predicates.Add($"cp.span_id = {Bind(criteria, scopedSpan)}");
        }

        if (!string.IsNullOrEmpty(part))
        {
            predicates.Add($"cp.bucket = {Bind(criteria, part)}");
        }

        string whereClause = " WHERE " + string.Join(" AND ", predicates);
        bool cutInSql = !full && budget > 0;
        string sql = cutInSql
            ? BuildBudgetedContentPartSelect("cp", whereClause, budget)
            : BuildContentPartSelect("cp", whereClause, "ORDER BY cp.span_id ASC, cp.position ASC");

        return QueryRows<StoredContentPart>(sql, criteria, static reader => new StoredContentPart(
            reader.GetString(reader.GetOrdinal("span_id")),
            ReadContentPartColumns(reader)));
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

        Dictionary<string, object?> criteria = new(StringComparer.Ordinal);
        List<string> predicates =
        [
            $"EXISTS (SELECT 1 FROM record_session_link rsl WHERE rsl.span_id = cp.span_id AND rsl.session_key = {Bind(criteria, sessionKey)})",
        ];
        if (!string.IsNullOrEmpty(scopedSpan))
        {
            predicates.Add($"cp.span_id = {Bind(criteria, scopedSpan)}");
        }

        if (!string.IsNullOrEmpty(part))
        {
            predicates.Add($"cp.bucket = {Bind(criteria, part)}");
        }

        // COUNT over the scoped rows only: no content-text column is selected, so the count read
        // transfers no part text however large the parts are.
        string sql =
            $"SELECT COUNT(*) AS part_count FROM content_part cp WHERE {string.Join(" AND ", predicates)}";

        IReadOnlyList<int> rows = QueryRows<int>(sql, criteria, static reader => reader.GetInt32(reader.GetOrdinal("part_count")));
        return rows.Count == 0 ? 0 : rows[0];
    }

    /// <inheritdoc />
    public IReadOnlyList<StoredToolDefinition> ToolDefinitions(string? spanId = null, string? sessionKey = null)
    {
        Dictionary<string, object?> parameters = new(StringComparer.Ordinal);
        string whereClause = BuildSpanFilterWhere("td", spanId, sessionKey, parameters);
        string sql =
            "SELECT td.span_id, td.position, td.tool_name, tds.server_name, td.schema_tokens, " +
            "tdt.tokenizer, tdt.is_model_own " +
            "FROM tool_definition td " +
            "LEFT JOIN tool_definition_server tds ON tds.span_id = td.span_id AND tds.position = td.position " +
            "LEFT JOIN tool_definition_tokenizer tdt ON tdt.span_id = td.span_id AND tdt.position = td.position" +
            $"{whereClause} ORDER BY td.span_id ASC, td.position ASC";
        return QueryRows<StoredToolDefinition>(sql, parameters, static reader => new StoredToolDefinition(
            reader.GetString(reader.GetOrdinal("span_id")),
            new CollectedToolDefinition(
                reader.GetInt32(reader.GetOrdinal("position")),
                reader.GetString(reader.GetOrdinal("tool_name")),
                reader.IsDBNull(reader.GetOrdinal("server_name")) ? null : reader.GetString(reader.GetOrdinal("server_name")),
                reader.GetInt64(reader.GetOrdinal("schema_tokens")),
                reader.IsDBNull(reader.GetOrdinal("tokenizer")) ? null : reader.GetString(reader.GetOrdinal("tokenizer")),
                !reader.IsDBNull(reader.GetOrdinal("is_model_own")) && reader.GetInt64(reader.GetOrdinal("is_model_own")) == 1L)));
    }

    /// <inheritdoc />
    public IReadOnlyList<StoredAgentInvocation> AgentInvocations(string? spanId = null, string? sessionKey = null)
    {
        Dictionary<string, object?> parameters = new(StringComparer.Ordinal);
        string whereClause = BuildSpanFilterWhere("ai", spanId, sessionKey, parameters);
        string sql = BuildAgentInvocationSelect("ai", whereClause, "ORDER BY ai.rowid ASC");
        return QueryRows<StoredAgentInvocation>(sql, parameters, static reader => new StoredAgentInvocation(
            reader.GetString(reader.GetOrdinal("span_id")),
            ReadAgentInvocationColumns(reader)));
    }

    /// <inheritdoc />
    public IReadOnlyList<StoredRecordSignals> RecordSignals(string? spanId = null, string? sessionKey = null)
    {
        Dictionary<string, object?> parameters = new(StringComparer.Ordinal);
        string filterClause = BuildSpanFilterWhere("sig", spanId, sessionKey, parameters);
        string sql =
            "WITH candidate_spans(span_id) AS (" +
            "SELECT span_id FROM record_error UNION " +
            "SELECT span_id FROM record_edit_failure UNION " +
            "SELECT span_id FROM record_correction UNION " +
            "SELECT span_id FROM record_declared_context_window" +
            ") " +
            "SELECT sig.span_id, re.message, ref.failure_count, rcr.correction_rule, rdcw.declared_context_window " +
            "FROM candidate_spans sig " +
            "LEFT JOIN record_error re ON re.span_id = sig.span_id " +
            "LEFT JOIN record_edit_failure ref ON ref.span_id = sig.span_id " +
            "LEFT JOIN record_correction_rule rcr ON rcr.span_id = sig.span_id " +
            "LEFT JOIN record_declared_context_window rdcw ON rdcw.span_id = sig.span_id" +
            $"{filterClause} ORDER BY sig.span_id ASC";
        return QueryRows<StoredRecordSignals>(sql, parameters, static reader => new StoredRecordSignals(
            reader.GetString(reader.GetOrdinal("span_id")),
            new CollectedRecordSignals(
                ReadNullableString(reader, "message"),
                ReadNullableInt64(reader, "failure_count"),
                ReadNullableString(reader, "correction_rule"),
                ReadNullableInt64(reader, "declared_context_window"))));
    }

    /// <inheritdoc />
    public IReadOnlyList<CollectedSession> Sessions(string? harnessId = null)
    {
        Dictionary<string, object?> parameters = new(StringComparer.Ordinal);
        if (harnessId is null)
        {
            return QuerySessions(sessionPredicate: null, usagePredicate: null, parameters);
        }

        string harnessMarker = Bind(parameters, harnessId);
        return QuerySessions(
            $"s.harness_id = {harnessMarker}",
            $"EXISTS (SELECT 1 FROM session s WHERE s.session_id = u.session_id AND s.harness_id = {harnessMarker})",
            parameters);
    }

    /// <inheritdoc />
    public IReadOnlyList<StoredSessionModelUsage> SessionModelUsages(string? sessionKey = null)
    {
        Dictionary<string, object?> parameters = new(StringComparer.Ordinal);
        string sql = "SELECT session_id, snapshot_position, model_id, token_class, token_count FROM session_model_usage";
        if (sessionKey is not null)
        {
            sql += " WHERE session_id = " + Bind(parameters, sessionKey);
        }

        sql += " ORDER BY session_id ASC, snapshot_position ASC, model_id ASC, token_class ASC";
        return QueryRows<StoredSessionModelUsage>(sql, parameters, static reader => new StoredSessionModelUsage(
            reader.GetString(reader.GetOrdinal("session_id")),
            new CollectedModelUsage(
                reader.GetInt32(reader.GetOrdinal("snapshot_position")),
                reader.GetString(reader.GetOrdinal("model_id")),
                ParseTokenClass(reader.GetString(reader.GetOrdinal("token_class"))),
                reader.GetInt64(reader.GetOrdinal("token_count")))));
    }

    /// <inheritdoc />
    public StoredFieldsInventoryView StoredFieldsInventory(string sessionKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionKey);

        Dictionary<string, object?> parameters = new(StringComparer.Ordinal);
        string sessionMarker = Bind(parameters, sessionKey);
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

        long[] counts = new long[tables.Count];
        Dictionary<string, List<StoredFieldRow>> keyRowsByTable = new(tables.Count, StringComparer.Ordinal);
        foreach (TableDefinition table in tables)
        {
            keyRowsByTable[table.Name] = [];
        }

        for (int tableIndex = 0; tableIndex < tables.Count; tableIndex++)
        {
            TableDefinition table = tables[tableIndex];
            string? whereClause = BuildStoredFieldsScopeWhere(table, sessionMarker);
            if (whereClause is null)
            {
                continue;
            }

            string countSql = $"SELECT COUNT(*) AS row_count FROM {table.Name} WHERE {whereClause}";
            IReadOnlyList<long> countRows = QueryRows<long>(countSql, parameters, static reader => reader.GetInt64(reader.GetOrdinal("row_count")));
            counts[tableIndex] = countRows.Count == 0 ? 0 : countRows[0];

            List<ColumnDefinition> keyColumns = keyColumnsByTable[table.Name];
            if (keyColumns.Count == 0)
            {
                continue;
            }

            string keySql =
                $"SELECT {string.Join(", ", keyColumns.Select(static column => column.Name))} " +
                $"FROM {table.Name} WHERE {whereClause} ORDER BY rowid ASC";
            IReadOnlyList<StoredFieldRow> keyRows = QueryRows<StoredFieldRow>(keySql, parameters, reader =>
            {
                StoredFieldCell[] cells = new StoredFieldCell[table.Columns.Count];
                for (int columnIndex = 0; columnIndex < table.Columns.Count; columnIndex++)
                {
                    ColumnDefinition every = table.Columns[columnIndex];
                    cells[columnIndex] = new StoredFieldCell(every.Name, every.Type);
                }

                int[] keyCellIndices = keyCellIndicesByTable[table.Name];
                for (int keyIndex = 0; keyIndex < keyColumns.Count; keyIndex++)
                {
                    cells[keyCellIndices[keyIndex]] = ReadStoredFieldCell(reader, keyColumns[keyIndex]);
                }

                return new StoredFieldRow(cells);
            });
            keyRowsByTable[table.Name].AddRange(keyRows);
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
        Dictionary<string, object?> parameters = new(StringComparer.Ordinal);
        string sessionMarker = Bind(parameters, sessionKey);
        string? whereClause = BuildStoredFieldsScopeWhere(definition, sessionMarker);
        if (whereClause is null)
        {
            // A table the session scope cannot bind answers an empty page, as the inventory's
            // zero count does for the same table.
            return new StoredTableRows(definition.Name, columnNames, 0, page, limit, offset, []);
        }

        string countSql = $"SELECT COUNT(*) AS row_count FROM {definition.Name} WHERE {whereClause}";
        IReadOnlyList<long> countRows = QueryRows<long>(countSql, parameters, static reader => reader.GetInt64(reader.GetOrdinal("row_count")));
        long totalRows = countRows.Count == 0 ? 0 : countRows[0];

        string limitMarker = Bind(parameters, limit);
        string offsetMarker = Bind(parameters, offset);
        string sql =
            $"SELECT {string.Join(", ", definition.Columns.Select(static column => column.Name))} " +
            $"FROM {definition.Name} WHERE {whereClause} ORDER BY rowid ASC LIMIT {limitMarker} OFFSET {offsetMarker}";
        IReadOnlyList<StoredFieldRow> rows = QueryRows<StoredFieldRow>(sql, parameters, reader => ReadStoredFieldRow(reader, definition));

        return new StoredTableRows(definition.Name, columnNames, totalRows, page, limit, offset, [.. rows]);
    }

    /// <inheritdoc />
    public IReadOnlyList<StoredSessionProjection> ListSessionProjections(string? harnessId = null)
    {
        Dictionary<string, object?> parameters = new(StringComparer.Ordinal);
        if (harnessId is null)
        {
            return QuerySessionProjections(filterPredicate: null, parameters);
        }

        string marker = Bind(parameters, harnessId);
        return QuerySessionProjections($"EXISTS (SELECT 1 FROM session s WHERE s.session_id = sp.session_id AND s.harness_id = {marker})", parameters);
    }

    /// <inheritdoc />
    public SessionHeaderPageResult SessionHeaderPage(string? harnessId, string? search, int page, int limit)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(page, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);

        Dictionary<string, object?> parameters = new(StringComparer.Ordinal);
        StringBuilder sql = new();
        AppendSessionRootRule(sql, harnessId, parameters);
        sql.Append("page_set AS (SELECT sc.session_id, sp.started_at_utc FROM scope sc JOIN session_projection sp ON sp.session_id = sc.session_id WHERE (");
        sql.Append("sc.parent_session_id IS NULL OR sc.parent_session_id = '' OR NOT EXISTS (SELECT 1 FROM scope p WHERE p.session_id = sc.parent_session_id) OR sc.session_id IN ");
        sql.Append(CyclicSessionSet);
        sql.Append(')');
        if (!string.IsNullOrEmpty(search))
        {
            // The search text is bound as one parameter, so a quote in it is data, and the ESCAPE
            // clause makes a %, _ or backslash in it match itself.
            string pattern = Bind(parameters, "%" + EscapeLikePattern(search) + "%");
            sql.Append(CultureInfo.InvariantCulture, $" AND (sp.label LIKE {pattern} ESCAPE '\\' OR sp.session_id LIKE {pattern} ESCAPE '\\')");
        }

        sql.Append(')');
        return ReadSessionHeaderPage(sql, "DESC", page, limit, parameters);
    }

    /// <inheritdoc />
    public SessionHeaderPageResult SessionChildHeaderPage(string parentSessionId, int page, int limit)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(parentSessionId);
        ArgumentOutOfRangeException.ThrowIfLessThan(page, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);

        Dictionary<string, object?> parameters = new(StringComparer.Ordinal);
        StringBuilder sql = new();
        AppendSessionRootRule(sql, harnessId: null, parameters);
        sql.Append("page_set AS (SELECT sp.session_id, sp.started_at_utc FROM session_projection_parent spp JOIN session_projection sp ON sp.session_id = spp.session_id WHERE ");
        AppendSessionAttachPredicate(sql, $"spp.parent_session_id = {Bind(parameters, parentSessionId)}");
        sql.Append(')');
        return ReadSessionHeaderPage(sql, "ASC", page, limit, parameters);
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

        Dictionary<string, object?> parameters = new(StringComparer.Ordinal);
        StringBuilder sql = new();
        AppendSessionRootRule(sql, harnessId: null, parameters);
        sql.Append("ranked AS (SELECT spp.parent_session_id, sp.session_id, sp.started_at_utc, ");
        sql.Append("row_number() OVER (PARTITION BY spp.parent_session_id ORDER BY sp.started_at_utc ASC, sp.session_id ASC) AS child_rank, ");
        sql.Append("COUNT(*) OVER (PARTITION BY spp.parent_session_id) AS child_total ");
        sql.Append("FROM session_projection_parent spp JOIN session_projection sp ON sp.session_id = spp.session_id WHERE ");
        AppendSessionAttachPredicate(sql, $"spp.parent_session_id IN ({SessionKeyListFilter(parents, parameters)})");
        sql.Append(CultureInfo.InvariantCulture, $"), preview AS (SELECT * FROM ranked WHERE child_rank <= {IntSql(perParent)}) ");
        sql.Append("SELECT r.parent_session_id, r.child_total, r.child_rank, r.session_id ");
        sql.Append("FROM preview r JOIN session_projection p ON p.session_id = r.parent_session_id ");
        sql.Append("ORDER BY p.started_at_utc DESC, r.parent_session_id ASC, r.child_rank ASC");

        IReadOnlyList<(string ParentSessionId, int TotalChildren, int ChildRank, string ChildSessionId)> rows =
            QueryRows<(string ParentSessionId, int TotalChildren, int ChildRank, string ChildSessionId)>(
                sql.ToString(),
                parameters,
                static reader => (
                    reader.GetString(reader.GetOrdinal("parent_session_id")),
                    reader.GetInt32(reader.GetOrdinal("child_total")),
                    reader.GetInt32(reader.GetOrdinal("child_rank")),
                    reader.GetString(reader.GetOrdinal("session_id"))));
        List<(string ParentSessionId, int TotalChildren, List<string> Children)> previews = [];
        foreach ((string parentSessionId, int totalChildren, int childRank, string childSessionId) in rows)
        {
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

        Dictionary<string, object?> parameters = new(StringComparer.Ordinal);
        string sql =
            "SELECT " + SessionProjectionHeaderColumns + " " +
            "FROM session_projection sp " + SessionProjectionHeaderJoins + " " +
            $"WHERE sp.session_id IN ({SessionKeyListFilter(keys, parameters)}) ORDER BY sp.started_at_utc DESC, sp.session_id ASC";
        return QueryRows<StoredSessionHeader>(sql, parameters, static reader => ReadSessionHeader(reader, 0));
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
    private static void AppendSessionRootRule(StringBuilder sql, string? harnessId, Dictionary<string, object?> parameters)
    {
        sql.Append("WITH RECURSIVE scope(session_id, parent_session_id) AS (SELECT sp.session_id, spp.parent_session_id FROM session_projection sp LEFT JOIN session_projection_parent spp ON spp.session_id = sp.session_id");
        if (harnessId is not null)
        {
            sql.Append(CultureInfo.InvariantCulture, $" WHERE EXISTS (SELECT 1 FROM session s WHERE s.session_id = sp.session_id AND s.harness_id = {Bind(parameters, harnessId)})");
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
    /// <param name="parameters">Parameter values the read binds.</param>
    /// <returns>The page's headers and the size of <c>page_set</c>.</returns>
    private SessionHeaderPageResult ReadSessionHeaderPage(StringBuilder cte, string order, int page, int limit, Dictionary<string, object?> parameters)
    {
        long offset = ((long)page - 1) * limit;
        string limitMarker = Bind(parameters, limit);
        string offsetMarker = Bind(parameters, offset);
        string sql = cte +
                     "SELECT (SELECT COUNT(*) FROM page_set) AS page_total, " + SessionProjectionHeaderColumns + " " +
                     "FROM (SELECT session_id FROM page_set ORDER BY started_at_utc " + order + ", session_id ASC" +
                     $" LIMIT {limitMarker} OFFSET {offsetMarker}) page " +
                     "JOIN session_projection sp ON sp.session_id = page.session_id " + SessionProjectionHeaderJoins + " " +
                     "ORDER BY sp.started_at_utc " + order + ", sp.session_id ASC";

        IReadOnlyList<(StoredSessionHeader Header, int Total)> rows =
            QueryRows<(StoredSessionHeader Header, int Total)>(
                sql,
                parameters,
                static reader => (ReadSessionHeader(reader, 1), reader.GetInt32(reader.GetOrdinal("page_total"))));
        List<StoredSessionHeader> headers = [];
        int total = 0;
        foreach ((StoredSessionHeader header, int rowTotal) in rows)
        {
            headers.Add(header);
            total = rowTotal;
        }

        if (headers.Count == 0)
        {
            string countSql = cte + "SELECT COUNT(*) AS page_total FROM page_set";
            IReadOnlyList<int> countRows = QueryRows<int>(countSql, parameters, static reader => reader.GetInt32(reader.GetOrdinal("page_total")));
            if (countRows.Count != 1)
            {
                throw new StoredDataException($"Expected 1 count row for the session header page, got {countRows.Count.ToString(CultureInfo.InvariantCulture)}.");
            }

            total = countRows[0];
        }

        return new SessionHeaderPageResult(headers, total);
    }

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

    /// <inheritdoc />
    public StoredRun? GetRun(string runId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        Dictionary<string, object?> parameters = new(StringComparer.Ordinal);
        IReadOnlyList<StoredRun> runs = QueryRuns($"r.run_id = {Bind(parameters, runId)}", parameters);
        return runs.Count == 0 ? null : runs[0];
    }

    /// <inheritdoc />
    public IReadOnlyList<StoredRun> ListRuns(string? harnessId = null)
    {
        Dictionary<string, object?> parameters = new(StringComparer.Ordinal);
        return QueryRuns(harnessId is null ? null : $"r.harness_id = {Bind(parameters, harnessId)}", parameters);
    }

    /// <inheritdoc />
    public StoredExecution? GetExecution(string executionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executionId);
        Dictionary<string, object?> parameters = new(StringComparer.Ordinal);
        IReadOnlyList<StoredExecution> executions = QueryExecutions($"e.execution_id = {Bind(parameters, executionId)}", parameters);
        return executions.Count == 0 ? null : executions[0];
    }

    /// <inheritdoc />
    public IReadOnlyList<StoredExecution> ListExecutionsForRun(string runId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        Dictionary<string, object?> parameters = new(StringComparer.Ordinal);
        return QueryExecutions($"e.run_id = {Bind(parameters, runId)}", parameters);
    }

    /// <inheritdoc />
    public IReadOnlyList<StoredExecution> ListExecutionsByHarness(string harnessId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(harnessId);
        Dictionary<string, object?> parameters = new(StringComparer.Ordinal);
        return QueryExecutions($"e.harness_id = {Bind(parameters, harnessId)}", parameters);
    }

    /// <inheritdoc />
    public StoredHarnessRollup? GetHarnessRollup(string harnessId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(harnessId);
        Dictionary<string, object?> parameters = new(StringComparer.Ordinal);
        IReadOnlyList<StoredHarnessRollup> rollups = QueryHarnessRollups($"harness_id = {Bind(parameters, harnessId)}", parameters);
        return rollups.Count == 0 ? null : rollups[0];
    }

    /// <inheritdoc />
    public IReadOnlyList<StoredHarnessRollup> ListHarnessRollups() =>
        QueryHarnessRollups(filterPredicate: null, new Dictionary<string, object?>(StringComparer.Ordinal));

    /// <inheritdoc />
    public StoredFinding? GetFinding(string findingId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(findingId);
        Dictionary<string, object?> parameters = new(StringComparer.Ordinal);
        IReadOnlyList<StoredFinding> findings = QueryFindings($"f.finding_id = {Bind(parameters, findingId)}", parameters);
        return findings.Count == 0 ? null : findings[0];
    }

    /// <inheritdoc />
    public IReadOnlyList<StoredFinding> ListFindings(string? runId = null, string? sessionId = null)
    {
        Dictionary<string, object?> parameters = new(StringComparer.Ordinal);
        List<string> predicates = [];
        if (runId is not null)
        {
            predicates.Add($"fr.run_id = {Bind(parameters, runId)}");
        }

        if (sessionId is not null)
        {
            predicates.Add($"fs.session_id = {Bind(parameters, sessionId)}");
        }

        string? filter = predicates.Count > 0 ? string.Join(" AND ", predicates) : null;
        return QueryFindings(filter, parameters);
    }

    /// <inheritdoc />
    public StoredPrediction? GetPrediction(string predictionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(predictionId);
        Dictionary<string, object?> parameters = new(StringComparer.Ordinal);
        IReadOnlyList<StoredPrediction> predictions = QueryPredictions($"p.prediction_id = {Bind(parameters, predictionId)}", parameters);
        return predictions.Count == 0 ? null : predictions[0];
    }

    /// <inheritdoc />
    public IReadOnlyList<StoredPrediction> ListPredictions(string? runId = null, string? findingId = null)
    {
        Dictionary<string, object?> parameters = new(StringComparer.Ordinal);
        List<string> predicates = [];
        if (runId is not null)
        {
            predicates.Add($"p.run_id = {Bind(parameters, runId)}");
        }

        if (findingId is not null)
        {
            predicates.Add($"p.finding_id = {Bind(parameters, findingId)}");
        }

        string? filter = predicates.Count > 0 ? string.Join(" AND ", predicates) : null;
        return QueryPredictions(filter, parameters);
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
        Dictionary<string, object?> parameters = new(StringComparer.Ordinal);
        string filter = SessionKeyListFilter(keys, parameters);
        IReadOnlyList<CollectedSession> sessions = QuerySessions($"s.session_id IN ({filter})", $"u.session_id IN ({filter})", parameters);
        if (sessions.Count == 0)
        {
            return [];
        }

        return BuildSessionListFigures(sessions, QuerySessionListProjectionShapes(filter, parameters), pricing);
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
}

/// <summary>
/// Session figure and section reads over a SQLite relational store.
/// </summary>
public abstract class SqliteRelationalStoreReadFigureBase : SqliteRelationalStoreReadCore
{
    /// <summary>Initializes the figure and section reads over <paramref name="databasePath"/>.</summary>
    /// <param name="databasePath">Path to the SQLite database file.</param>
    /// <param name="circuitBreaker">Circuit breaker guarding every <c>sqlite3</c> invocation.</param>
    /// <param name="fileSystem">Filesystem seam; defaults to <see cref="PhysicalRelationalStoreFileSystem.Instance"/> when <see langword="null"/>.</param>
    protected SqliteRelationalStoreReadFigureBase(
        string databasePath,
        ICircuitBreaker circuitBreaker,
        IRelationalStoreFileSystem? fileSystem)
        : base(databasePath, circuitBreaker, fileSystem)
    {
    }

    /// <inheritdoc />
    public IReadOnlyList<StoredSkillInvocation> SkillsInvoked(string? spanId = null, string? sessionKey = null)
    {
        Dictionary<string, object?> parameters = new(StringComparer.Ordinal);
        string whereClause = BuildSpanFilterWhere("rsi", spanId, sessionKey, parameters);
        string sql =
            "SELECT rsi.span_id, rsi.skill_name " +
            $"FROM record_skill_invocation rsi{whereClause} ORDER BY rsi.rowid ASC";
        return QueryRows<StoredSkillInvocation>(sql, parameters, static reader => new StoredSkillInvocation(
            reader.GetString(reader.GetOrdinal("span_id")),
            reader.GetString(reader.GetOrdinal("skill_name"))));
    }

    /// <inheritdoc />
    public CollectedSession? GetSession(string sessionKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionKey);
        Dictionary<string, object?> parameters = new(StringComparer.Ordinal);
        string marker = Bind(parameters, sessionKey);
        IReadOnlyList<CollectedSession> sessions = QuerySessions($"s.session_id = {marker}", $"u.session_id = {marker}", parameters);
        return sessions.Count == 0 ? null : sessions[0];
    }

    /// <inheritdoc />
    public StoredSessionProjection? GetSessionProjection(string sessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        Dictionary<string, object?> parameters = new(StringComparer.Ordinal);
        string marker = Bind(parameters, sessionId);
        IReadOnlyList<StoredSessionProjection> projections = QuerySessionProjections($"sp.session_id = {marker}", parameters);
        return projections.Count == 0 ? null : projections[0];
    }

    /// <inheritdoc />
    public IReadOnlyList<StoredSessionTurn> GetSessionTurns(string sessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        Dictionary<string, object?> parameters = new(StringComparer.Ordinal);
        string marker = Bind(parameters, sessionId);
        string sql = $"SELECT session_id, position, span_id FROM session_turn WHERE session_id = {marker} ORDER BY position ASC";
        return QueryRows<StoredSessionTurn>(sql, parameters, static reader => new StoredSessionTurn(
            reader.GetString(reader.GetOrdinal("session_id")),
            reader.GetInt32(reader.GetOrdinal("position")),
            reader.GetString(reader.GetOrdinal("span_id"))));
    }

    private protected IReadOnlyList<SessionListFigure> BuildSessionListFigures(
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
    /// <param name="parameters">Parameter values the read binds.</param>
    /// <returns>Each named session that has a projection, by session id.</returns>
    private protected Dictionary<string, SessionListProjectionShape> QuerySessionListProjectionShapes(string filter, Dictionary<string, object?> parameters)
    {
        HashSet<string> projectedSessionIds = [];
        foreach (string sessionId in QueryRows<string>(
                     $"SELECT sp.session_id FROM session_projection sp WHERE sp.session_id IN ({filter})",
                     parameters,
                     static reader => reader.GetString(0)))
        {
            _ = projectedSessionIds.Add(sessionId);
        }

        Dictionary<string, List<string>> requestsBySession = new(StringComparer.Ordinal);
        foreach ((string SessionId, string RequestSpanId) row in QueryRows<(string SessionId, string RequestSpanId)>(
                     $"SELECT session_id, request_span_id FROM session_request WHERE session_id IN ({filter}) ORDER BY session_id ASC, position ASC",
                     parameters,
                     static reader => (reader.GetString(0), reader.GetString(1))))
        {
            GetOrCreateList(requestsBySession, row.SessionId).Add(row.RequestSpanId);
        }

        Dictionary<string, List<StoredSessionTurn>> turnsBySession = new(StringComparer.Ordinal);
        foreach ((string SessionId, int Position, string SpanId) row in QueryRows<(string SessionId, int Position, string SpanId)>(
                     $"SELECT session_id, position, span_id FROM session_turn WHERE session_id IN ({filter}) ORDER BY session_id ASC, position ASC",
                     parameters,
                     static reader => (reader.GetString(0), reader.GetInt32(1), reader.GetString(2))))
        {
            GetOrCreateList(turnsBySession, row.SessionId).Add(new StoredSessionTurn(row.SessionId, row.Position, row.SpanId));
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
        Dictionary<string, object?> parameters = new(StringComparer.Ordinal);
        string filter = SessionKeyListFilter(keys, parameters);
        IReadOnlyList<CollectedSession> sessions = QuerySessions($"s.session_id IN ({filter})", $"u.session_id IN ({filter})", parameters);
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
    private protected static SessionReportFigure BuildSessionReportFigure(
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
    public SessionRequestFigurePageResult SessionRequestFigures(string sessionKey, int page, int limit, string order = "desc")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionKey);
        ArgumentOutOfRangeException.ThrowIfLessThan(page, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        string sqlOrder = NormalizeSortOrderSql(order);

        Dictionary<string, object?> parameters = new(StringComparer.Ordinal);
        string key = Bind(parameters, sessionKey);
        StringBuilder ctes = new("WITH RECURSIVE ");
        AppendSessionSectionCtes(ctes, key);
        ctes.Append(CultureInfo.InvariantCulture, $"named_requests(position, request_span_id) AS (SELECT position, request_span_id FROM session_request WHERE session_id = {key}), ");
        ctes.Append("fallback_requests(position, request_span_id) AS (SELECT ROW_NUMBER() OVER (ORDER BY rr.started_at_utc ASC, rr.span_id ASC), rr.span_id FROM roots ro JOIN record rr ON rr.span_id = ro.span_id WHERE NOT EXISTS (SELECT 1 FROM session_request WHERE session_id = ");
        ctes.Append(key);
        ctes.Append(")), ");
        ctes.Append("all_requests(position, request_span_id) AS (SELECT position, request_span_id FROM named_requests WHERE EXISTS (SELECT 1 FROM session_request WHERE session_id = ");
        ctes.Append(key);
        ctes.Append(") UNION ALL SELECT position, request_span_id FROM fallback_requests) ");

        StringBuilder sql = new();
        sql.Append(ctes);
        string limitMarker = Bind(parameters, limit);
        string offsetMarker = Bind(parameters, ((long)page - 1) * limit);
        sql.Append(CultureInfo.InvariantCulture, $", page(position, request_span_id) AS (SELECT position, request_span_id FROM all_requests ORDER BY position {sqlOrder} LIMIT {limitMarker} OFFSET {offsetMarker}), ");
        sql.Append("subtree(root, span) AS (SELECT request_span_id, request_span_id FROM page UNION SELECT s.root, ch.span_id FROM subtree s JOIN record_parent ch ON ch.parent_span_id = s.span AND ch.span_id IN (SELECT span_id FROM spans)), ");
        sql.Append("owned(root, turn_position, turn_span) AS (SELECT s.root, t.position, t.span_id FROM subtree s JOIN turn_entries t ON t.span_id = s.span), ");
        sql.Append("ranked(root, turn_position, turn_span, turn_rank, turn_total) AS (SELECT root, turn_position, turn_span, ROW_NUMBER() OVER (PARTITION BY root ORDER BY turn_position ASC), COUNT(*) OVER (PARTITION BY root) FROM owned), ");
        sql.Append("first_model(root, model_id) AS (SELECT root, model_id FROM (SELECT o.root, rm.model_id, ROW_NUMBER() OVER (PARTITION BY o.root ORDER BY o.turn_position ASC) AS model_rank FROM owned o JOIN record_model rm ON rm.span_id = o.turn_span) WHERE model_rank = 1) ");

        sql.Append("SELECT (SELECT COUNT(*) FROM all_requests) AS page_total, p.request_span_id AS request_span_id, ");
        sql.Append("rr.started_at_utc AS request_started_at_utc, rm.model_id AS request_model_id, ");
        sql.Append("COALESCE(rk.turn_total, 0) AS turn_total, ");
        sql.Append("fts.started_at_utc AS first_turn_started_at_utc, fmm.model_id AS first_turn_model_id ");
        sql.Append("FROM page p ");
        sql.Append("LEFT JOIN record rr ON rr.span_id = p.request_span_id ");
        sql.Append("LEFT JOIN record_model rm ON rm.span_id = p.request_span_id ");
        sql.Append("LEFT JOIN (SELECT root, MAX(turn_total) AS turn_total FROM ranked GROUP BY root) rk ON rk.root = p.request_span_id ");
        sql.Append("LEFT JOIN (SELECT root, turn_span FROM ranked WHERE turn_rank = 1) ft ON ft.root = p.request_span_id ");
        sql.Append("LEFT JOIN record fts ON fts.span_id = ft.turn_span ");
        sql.Append("LEFT JOIN first_model fmm ON fmm.root = p.request_span_id ");
        sql.Append(CultureInfo.InvariantCulture, $"ORDER BY p.position {sqlOrder}");

        IReadOnlyList<ReadTimeSessionRequestSummary> rows = QueryRows<ReadTimeSessionRequestSummary>(
            sql.ToString(),
            parameters,
            static reader =>
            {
                string? requestStartedRaw = ReadNullableString(reader, "request_started_at_utc");
                DateTimeOffset? requestStarted = requestStartedRaw is null ? null : ParseTimestampRequired(requestStartedRaw, "record.started_at_utc");
                string? requestModel = ReadNullableString(reader, "request_model_id");
                string? firstTurnStartedRaw = ReadNullableString(reader, "first_turn_started_at_utc");
                DateTimeOffset? firstTurnStarted = firstTurnStartedRaw is null ? null : ParseTimestampRequired(firstTurnStartedRaw, "owned turn started_at_utc");
                string? firstTurnModel = ReadNullableString(reader, "first_turn_model_id");
                string? model = string.IsNullOrEmpty(requestModel) ? firstTurnModel : requestModel;
                string timestampUtc = requestStarted.HasValue
                    ? FormatTimestamp(requestStarted.Value)
                    : (firstTurnStarted.HasValue ? FormatTimestamp(firstTurnStarted.Value) : string.Empty);
                return new ReadTimeSessionRequestSummary(
                    RequestSpanId: reader.GetString(reader.GetOrdinal("request_span_id")),
                    TimestampUtc: timestampUtc,
                    Turns: reader.GetInt32(reader.GetOrdinal("turn_total")),
                    Model: model,
                    PromptPreview: null);
            });

        int total = QueryRows<int>(
            ctes + "SELECT (SELECT COUNT(*) FROM all_requests) AS page_total",
            parameters,
            static reader => reader.GetInt32(reader.GetOrdinal("page_total")))[0];

        return new SessionRequestFigurePageResult(rows, total);
    }

    private protected static string NormalizeSortOrderSql(string order) => SectionSortOrder.ToSqlDirection(order);

    /// <inheritdoc />
    public SessionReconciliationFigurePageResult SessionReconciliationFigures(string sessionKey, int page, int limit, string order = "desc")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionKey);
        ArgumentOutOfRangeException.ThrowIfLessThan(page, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        string sqlOrder = NormalizeSortOrderSql(order);

        Dictionary<string, object?> parameters = new(StringComparer.Ordinal);
        string key = Bind(parameters, sessionKey);
        StringBuilder ctes = new("WITH RECURSIVE ");
        AppendSessionSectionCtes(ctes, key);
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

        string limitMarker = Bind(parameters, limit);
        string offsetMarker = Bind(parameters, ((long)page - 1) * limit);
        StringBuilder sql = new();
        sql.Append(ctes);
        sql.Append("SELECT (SELECT COUNT(*) FROM measurable) AS page_total, position, span_id, ");
        sql.Append("reported_input, ");
        sql.Append("sum_chat_input, input_match, ");
        sql.Append("reported_output, ");
        sql.Append("sum_chat_output, output_match ");
        sql.Append(CultureInfo.InvariantCulture, $"FROM measurable ORDER BY position {sqlOrder} LIMIT {limitMarker} OFFSET {offsetMarker}");

        IReadOnlyList<ReadTimeTurnReconciliation> rows = QueryRows<ReadTimeTurnReconciliation>(
            sql.ToString(),
            parameters,
            static reader => new ReadTimeTurnReconciliation(
                RequestSpanId: reader.GetString(reader.GetOrdinal("span_id")),
                RootInput: ReadNullableInt64(reader, "reported_input"),
                SumChatInput: reader.GetInt64(reader.GetOrdinal("sum_chat_input")),
                InputMatch: reader.GetInt64(reader.GetOrdinal("input_match")) == 1L,
                RootOutput: ReadNullableInt64(reader, "reported_output"),
                SumChatOutput: reader.GetInt64(reader.GetOrdinal("sum_chat_output")),
                OutputMatch: reader.GetInt64(reader.GetOrdinal("output_match")) == 1L));

        int total = rows.Count == 0
            ? ReadSessionSectionTotal(ctes, "measurable", "session reconciliation page", parameters)
            : QueryRows<int>(ctes + "SELECT COUNT(*) AS page_total FROM measurable", parameters, static reader => reader.GetInt32(reader.GetOrdinal("page_total")))[0];

        return new SessionReconciliationFigurePageResult(rows, total);
    }

    /// <inheritdoc />
    public SessionProblemPageResult SessionProblems(string sessionKey, int page, int limit, string order = "desc")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionKey);
        ArgumentOutOfRangeException.ThrowIfLessThan(page, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        string sqlOrder = NormalizeSortOrderSql(order);

        Dictionary<string, object?> parameters = new(StringComparer.Ordinal);
        string key = Bind(parameters, sessionKey);
        StringBuilder sql = new();
        sql.Append("SELECT (SELECT COUNT(*) FROM (SELECT DISTINCT problem_id FROM (");
        AppendSessionProblemAttachment(sql, key);
        sql.Append(")) att) AS page_total, ");
        sql.Append("ip.severity, ip.code, ip.message, ");
        sql.Append("ipl.location, ");
        sql.Append("ips.span_id AS problem_span_id, ");
        sql.Append("ipst.stack ");
        sql.Append("FROM ingest_problem ip ");
        sql.Append("LEFT JOIN ingest_problem_location ipl ON ipl.problem_id = ip.problem_id ");
        sql.Append("LEFT JOIN ingest_problem_span ips ON ips.problem_id = ip.problem_id ");
        sql.Append("LEFT JOIN ingest_problem_stack ipst ON ipst.problem_id = ip.problem_id ");
        sql.Append("LEFT JOIN ingest_problem_timestamp ipt ON ipt.problem_id = ip.problem_id ");
        sql.Append("WHERE ip.problem_id IN (SELECT DISTINCT problem_id FROM (");
        AppendSessionProblemAttachment(sql, key);
        sql.Append(")) ");
        string limitMarker = Bind(parameters, limit);
        string offsetMarker = Bind(parameters, ((long)page - 1) * limit);
        sql.Append(CultureInfo.InvariantCulture, $"ORDER BY CASE WHEN ipt.timestamp_utc IS NULL THEN 1 ELSE 0 END ASC, ipt.timestamp_utc {sqlOrder}, ip.problem_id {sqlOrder} LIMIT {limitMarker} OFFSET {offsetMarker}");

        IReadOnlyList<Problem> rows = QueryRows<Problem>(
            sql.ToString(),
            parameters,
            static reader =>
            {
                string? location = ReadNullableString(reader, "location") ?? ReadNullableString(reader, "problem_span_id");
                return new Problem(
                    ParseProblemSeverityValue(reader.GetString(reader.GetOrdinal("severity"))),
                    reader.GetString(reader.GetOrdinal("code")),
                    reader.GetString(reader.GetOrdinal("message")),
                    location,
                    ReadNullableString(reader, "stack"));
            });

        int total;
        StringBuilder countSql = new();
        countSql.Append("SELECT COUNT(*) AS page_total FROM (SELECT DISTINCT problem_id FROM (");
        AppendSessionProblemAttachment(countSql, key);
        countSql.Append("))");
        IReadOnlyList<int> countRows = QueryRows<int>(countSql.ToString(), parameters, static reader => reader.GetInt32(reader.GetOrdinal("page_total")));
        if (countRows.Count != 1)
        {
            throw new StoredDataException($"Expected 1 count row for the session problem page, got {countRows.Count.ToString(CultureInfo.InvariantCulture)}.");
        }

        total = countRows[0];

        return new SessionProblemPageResult(rows, total);
    }

    /// <inheritdoc />
    public SessionToolFigureSet SessionToolFigures(string sessionKey, IModelPricingLookup? pricing = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionKey);
        Dictionary<string, object?> parameters = new(StringComparer.Ordinal);
        string key = Bind(parameters, sessionKey);

        // One query per tagged select: the two identity columns the unused-range pricing
        // resolves (the session's harness and its first turn model), the tool-call metadata, the
        // tool-definition metadata, and the stored schema projection. No content column, no
        // content-text table, no timeline, and no context row is touched.
        string? harnessId = QuerySingleNullableString(
            $"SELECT s.harness_id FROM session s WHERE s.session_id = {key}",
            parameters);
        string? primaryModel = QuerySingleNullableString(
            "SELECT rm.model_id FROM record_session_link rsl JOIN record r ON r.span_id = rsl.span_id " +
            "JOIN record_model rm ON rm.span_id = r.span_id " +
            $"WHERE rsl.session_key = {key} AND r.op = 'llm.invoke' " +
            "ORDER BY r.started_at_utc ASC, r.span_id ASC LIMIT 1",
            parameters);

        SessionToolCallAggregate toolCalls = ReadSessionToolCalls(parameters, key);
        SessionToolDefinitionAggregate definitions = ReadSessionToolDefinitions(parameters, key);
        List<StoredSchemaRankedTool> rankedTools = ReadSessionSchemaRankedTools(parameters, key);
        List<StoredSchemaServerTokens> schemaServers = ReadSessionSchemaServers(parameters, key);
        (StoredSessionSchema? schema, string? schemaUnmeasurableReason) = ReadSessionSchema(
            parameters,
            key,
            rankedTools,
            schemaServers);

        // The residency helpers price the stored schema only, so the read carries the schema in a
        // minimal projection shape instead of reading the session's whole projection.
        StoredSessionProjection? projection = BuildSessionSchemaProjection(sessionKey, schema);

        List<ReadTimeSessionToolSummary> tools = BuildReaderToolSummaries(
            projection,
            schema?.TurnCount ?? 0,
            definitions.DefStats,
            toolCalls.ToolsInvoked,
            toolCalls.InvCounts,
            toolCalls.ErrCounts);
        List<ReadTimeSessionServerSummary> servers = BuildReaderServerSummaries(projection, tools);
        UnusedSchemaRange? unusedRange = ResolveReaderUnusedRange(
            projection,
            harnessId ?? string.Empty,
            primaryModel is null ? [] : [primaryModel],
            pricing);
        List<StoredSchemaServerTokens> byServer = BuildSessionToolServerTokens(schema, tools);
        (long? maxCost, long residentCostTotal, int unusedCount) = SummarizeSessionTools(tools);

        return new SessionToolFigureSet(
            Tools: tools,
            Servers: servers,
            ToolsOffered: definitions.ToolsOffered,
            UnusedRange: unusedRange,
            ByServer: byServer,
            MaxCost: maxCost,
            ResidentCostTotal: residentCostTotal,
            InvocationCount: toolCalls.ToolCallTotal,
            UnusedCount: unusedCount,
            DefinitionsSpanId: definitions.DefinitionsSpanId,
            SchemaMeasurable: schema is not null,
            SchemaUnmeasurableReason: schemaUnmeasurableReason);
    }

    private protected string? QuerySingleNullableString(string sql, Dictionary<string, object?> parameters)
    {
        IReadOnlyList<string?> rows = QueryRows<string?>(
            sql,
            parameters,
            static reader => reader.IsDBNull(0) ? null : reader.GetString(0));
        return rows.Count == 0 ? null : rows[0];
    }

    private protected SessionToolCallAggregate ReadSessionToolCalls(Dictionary<string, object?> parameters, string key)
    {
        int toolCallTotal = 0;
        List<string> toolsInvoked = [];
        HashSet<string> seenInvokedTools = new(StringComparer.Ordinal);
        Dictionary<string, int> invCounts = new(StringComparer.Ordinal);
        Dictionary<string, int> errCounts = new(StringComparer.Ordinal);
        foreach ((string ToolName, bool? Succeeded, string? Error) row in QueryRows<(string ToolName, bool? Succeeded, string? Error)>(
                     "SELECT tc.tool_name, oco.succeeded, terr.message FROM tool_call tc " +
                     $"JOIN record_session_link rsl ON rsl.span_id = tc.span_id AND rsl.session_key = {key} " +
                     "LEFT JOIN tool_call_outcome oco ON oco.span_id = tc.span_id " +
                     "LEFT JOIN tool_call_error terr ON terr.span_id = tc.span_id ORDER BY tc.rowid ASC",
                     parameters,
                     static reader => (
                         reader.GetString(reader.GetOrdinal("tool_name")),
                         reader.IsDBNull(reader.GetOrdinal("succeeded")) ? null : reader.GetInt64(reader.GetOrdinal("succeeded")) == 1L,
                         reader.IsDBNull(reader.GetOrdinal("message")) ? null : reader.GetString(reader.GetOrdinal("message")))))
        {
            toolCallTotal++;
            if (seenInvokedTools.Add(row.ToolName))
            {
                toolsInvoked.Add(row.ToolName);
            }

            invCounts[row.ToolName] = invCounts.GetValueOrDefault(row.ToolName) + 1;
            if (row.Succeeded == false || row.Error is not null)
            {
                errCounts[row.ToolName] = errCounts.GetValueOrDefault(row.ToolName) + 1;
            }
        }

        return new SessionToolCallAggregate(toolCallTotal, toolsInvoked, invCounts, errCounts);
    }

    private protected SessionToolDefinitionAggregate ReadSessionToolDefinitions(Dictionary<string, object?> parameters, string key)
    {
        Dictionary<string, (long SchemaTokens, string? Server, HashSet<string> SpanIds)> defStats = new(StringComparer.Ordinal);
        List<string> toolsOffered = [];
        HashSet<string> seenOfferedTools = new(StringComparer.Ordinal);
        string? definitionsSpanId = null;
        foreach ((string SpanId, string ToolName, long SchemaTokens, string? Server) row in QueryRows<(string SpanId, string ToolName, long SchemaTokens, string? Server)>(
                     "SELECT td.span_id, td.tool_name, td.schema_tokens, tds.server_name FROM tool_definition td " +
                     $"JOIN record_session_link rsl ON rsl.span_id = td.span_id AND rsl.session_key = {key} " +
                     "JOIN record r ON r.span_id = td.span_id " +
                     "LEFT JOIN tool_definition_server tds ON tds.span_id = td.span_id AND tds.position = td.position " +
                     "ORDER BY r.rowid ASC, td.position ASC",
                     parameters,
                     static reader => (
                         reader.GetString(reader.GetOrdinal("span_id")),
                         reader.GetString(reader.GetOrdinal("tool_name")),
                         reader.GetInt64(reader.GetOrdinal("schema_tokens")),
                         reader.IsDBNull(reader.GetOrdinal("server_name")) ? null : reader.GetString(reader.GetOrdinal("server_name")))))
        {
            definitionsSpanId ??= row.SpanId;
            if (seenOfferedTools.Add(row.ToolName))
            {
                toolsOffered.Add(row.ToolName);
            }

            if (!defStats.TryGetValue(row.ToolName, out (long SchemaTokens, string? Server, HashSet<string> SpanIds) existing))
            {
                defStats[row.ToolName] = (row.SchemaTokens, row.Server, new HashSet<string>(StringComparer.Ordinal) { row.SpanId });
            }
            else
            {
                _ = existing.SpanIds.Add(row.SpanId);
                defStats[row.ToolName] = (existing.SchemaTokens, existing.Server ?? row.Server, existing.SpanIds);
            }
        }

        return new SessionToolDefinitionAggregate(toolsOffered, defStats, definitionsSpanId);
    }

    private protected List<StoredSchemaRankedTool> ReadSessionSchemaRankedTools(Dictionary<string, object?> parameters, string key)
    {
        List<StoredSchemaRankedTool> rankedTools = [];
        foreach (StoredSchemaRankedTool ranked in QueryRows<StoredSchemaRankedTool>(
                     "SELECT t.position, t.tool_name, t.cost_tokens, t.invoked, s.server_name " +
                     "FROM session_schema_ranked_tool t " +
                     "LEFT JOIN session_schema_ranked_tool_server s ON s.session_id = t.session_id AND s.position = t.position " +
                     $"WHERE t.session_id = {key} ORDER BY t.position ASC",
                     parameters,
                     static reader => new StoredSchemaRankedTool(
                         Position: reader.GetInt32(reader.GetOrdinal("position")),
                         ToolName: reader.GetString(reader.GetOrdinal("tool_name")),
                         CostTokens: reader.GetInt64(reader.GetOrdinal("cost_tokens")),
                         Invoked: reader.GetInt64(reader.GetOrdinal("invoked")) == 1L,
                         ServerName: reader.IsDBNull(reader.GetOrdinal("server_name")) ? null : reader.GetString(reader.GetOrdinal("server_name")))))
        {
            rankedTools.Add(ranked);
        }

        return rankedTools;
    }

    private protected List<StoredSchemaServerTokens> ReadSessionSchemaServers(Dictionary<string, object?> parameters, string key)
    {
        List<StoredSchemaServerTokens> schemaServers = [];
        foreach (StoredSchemaServerTokens server in QueryRows<StoredSchemaServerTokens>(
                     "SELECT t.server_name, t.tokens FROM session_schema_server t " +
                     $"WHERE t.session_id = {key} ORDER BY t.rowid ASC",
                     parameters,
                     static reader => new StoredSchemaServerTokens(
                         ServerName: reader.GetString(reader.GetOrdinal("server_name")),
                         Tokens: reader.GetInt64(reader.GetOrdinal("tokens")))))
        {
            schemaServers.Add(server);
        }

        return schemaServers;
    }

    private protected (StoredSessionSchema? Schema, string? UnmeasurableReason) ReadSessionSchema(
        Dictionary<string, object?> parameters,
        string key,
        List<StoredSchemaRankedTool> rankedTools,
        List<StoredSchemaServerTokens> schemaServers)
    {
        StoredSessionSchema? schema = null;
        IReadOnlyList<(int InvocationCount, int TurnCount, long? Unused)> schemaRows = QueryRows<(int InvocationCount, int TurnCount, long? Unused)>(
            "SELECT t.invocation_count, t.turn_count, u.token_residencies FROM session_schema t " +
            $"LEFT JOIN session_schema_unused u ON u.session_id = t.session_id WHERE t.session_id = {key}",
            parameters,
            static reader => (
                reader.GetInt32(reader.GetOrdinal("invocation_count")),
                reader.GetInt32(reader.GetOrdinal("turn_count")),
                ReadNullableInt64(reader, "token_residencies")));
        if (schemaRows.Count > 0)
        {
            schema = new StoredSessionSchema(
                InvocationCount: schemaRows[0].InvocationCount,
                TurnCount: schemaRows[0].TurnCount,
                RankedTools: rankedTools,
                Servers: schemaServers,
                UnusedTokenResidencies: schemaRows[0].Unused);
        }

        IReadOnlyList<string?> reasonRows = QueryRows<string?>(
            $"SELECT r.reason FROM session_schema_unmeasurable r WHERE r.session_id = {key}",
            parameters,
            static reader => reader.IsDBNull(0) ? null : reader.GetString(0));
        return (schema, reasonRows.Count == 0 ? null : reasonRows[0]);
    }

    private protected static StoredSessionProjection? BuildSessionSchemaProjection(string sessionKey, StoredSessionSchema? schema)
    {
        return schema is null
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
    }

    private protected static List<StoredSchemaServerTokens> BuildSessionToolServerTokens(
        StoredSessionSchema? schema,
        IReadOnlyList<ReadTimeSessionToolSummary> tools)
    {
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

        return byServer;
    }

    private protected static (long? MaxCost, long ResidentCostTotal, int UnusedCount) SummarizeSessionTools(
        IReadOnlyList<ReadTimeSessionToolSummary> tools)
    {
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

        return (maxCost, residentCostTotal, unusedCount);
    }

    private protected sealed record SessionToolCallAggregate(
        int ToolCallTotal,
        List<string> ToolsInvoked,
        Dictionary<string, int> InvCounts,
        Dictionary<string, int> ErrCounts);

    private protected sealed record SessionToolDefinitionAggregate(
        List<string> ToolsOffered,
        Dictionary<string, (long SchemaTokens, string? Server, HashSet<string> SpanIds)> DefStats,
        string? DefinitionsSpanId);

    /// <inheritdoc />
    public IReadOnlyList<StoredSkillInvocation> SessionSkillFigures(string sessionKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionKey);
        Dictionary<string, object?> parameters = new(StringComparer.Ordinal);
        string key = Bind(parameters, sessionKey);
        string sql =
            "SELECT rsi.span_id, rsi.skill_name " +
            "FROM record_skill_invocation rsi " +
            "JOIN record_session_link rsl ON rsl.span_id = rsi.span_id " +
            $"WHERE rsl.session_key = {key} ORDER BY rsi.rowid ASC";
        return QueryRows<StoredSkillInvocation>(sql, parameters, static reader => new StoredSkillInvocation(
            SpanId: reader.GetString(reader.GetOrdinal("span_id")),
            SkillName: reader.GetString(reader.GetOrdinal("skill_name"))));
    }

    /// <inheritdoc />
    public IReadOnlyList<StoredSkillListing> SessionSkillListings(string sessionKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionKey);
        Dictionary<string, object?> parameters = new(StringComparer.Ordinal);
        string key = Bind(parameters, sessionKey);
        string sql =
            "SELECT rsk.span_id, rsk.skill_name, COALESCE(st.position, -1) AS turn_position " +
            "FROM record_skill_listing rsk " +
            "JOIN record_session_link rsl ON rsl.span_id = rsk.span_id " +
            "JOIN record r ON r.span_id = rsk.span_id " +
            $"LEFT JOIN session_turn st ON st.span_id = rsk.span_id AND st.session_id = {key} " +
            $"WHERE rsl.session_key = {key} ORDER BY r.rowid ASC, rsk.position ASC";
        return QueryRows<StoredSkillListing>(sql, parameters, static reader =>
        {
            int turn = reader.GetInt32(reader.GetOrdinal("turn_position"));
            return new StoredSkillListing(
                SpanId: reader.GetString(reader.GetOrdinal("span_id")),
                SkillName: reader.GetString(reader.GetOrdinal("skill_name")),
                TurnIndex: turn >= 0 ? turn : null);
        });
    }

    /// <summary>
    /// The unsplit tool names a KW skill load is recorded under (D17). Together with the MCP
    /// split rule in <see cref="AppendSessionKwLoadPredicate"/>, this list is the statement's
    /// whole load filter, so the page and the total answer from SQL.
    /// </summary>
    private protected static readonly string[] KwSkillLoadToolNames =
    [
        "mcp__kw-docs__load_skill",
        "mcp__kw_docs__load_skill",
        "kw-docs_load_skill",
        "kw-docs__load_skill",
        "kw-docs/load_skill",
        "mcp__kyber-weave__load_skill",
        "mcp__kyber_weave__load_skill",
    ];

    /// <inheritdoc />
    public SessionKwSkillLoadPageResult SessionKwSkillLoads(string sessionKey, int page, int limit, string order = "desc")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionKey);
        ArgumentOutOfRangeException.ThrowIfLessThan(page, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        string sqlOrder = NormalizeSortOrderSql(order);
        // The statement reads the tool-call header, its MCP split, the first 1024 characters of
        // its reply and its record's start time for the session's load rows on the page only: the
        // D17 load rule is the statement's filter and the page answers from LIMIT/OFFSET, so a
        // page never reads the reply head of a load outside it. The call's input is never joined:
        // the skill is read from the reply header (D17).
        Dictionary<string, object?> parameters = new(StringComparer.Ordinal);
        string key = Bind(parameters, sessionKey);
        StringBuilder sql = new();
        sql.Append("SELECT (SELECT COUNT(*) FROM (SELECT tc.span_id FROM tool_call tc ");
        sql.Append("JOIN record r ON r.span_id = tc.span_id ");
        sql.Append("LEFT JOIN tool_call_mcp tcm ON tcm.span_id = tc.span_id ");
        AppendSessionKwLoadPredicate(sql, key, parameters);
        sql.Append(")) AS page_total, ");
        sql.Append("tc.span_id, tc.tool_name, ");
        sql.Append("tcm.server_name, ");
        sql.Append("tcm.server_tool_name, ");
        sql.Append("substr(tcrt.text, 1, 1024) AS reply_head, ");
        sql.Append("r.started_at_utc ");
        sql.Append("FROM tool_call tc ");
        sql.Append("JOIN record r ON r.span_id = tc.span_id ");
        sql.Append("LEFT JOIN tool_call_mcp tcm ON tcm.span_id = tc.span_id ");
        sql.Append("LEFT JOIN tool_call_result_text tcrt ON tcrt.span_id = tc.span_id ");
        AppendSessionKwLoadPredicate(sql, key, parameters);
        string limitMarker = Bind(parameters, limit);
        string offsetMarker = Bind(parameters, ((long)page - 1) * limit);
        sql.Append(CultureInfo.InvariantCulture, $" ORDER BY r.started_at_utc {sqlOrder}, tc.rowid {sqlOrder} LIMIT {limitMarker} OFFSET {offsetMarker}");

        IReadOnlyList<StoredKwSkillLoad> results = QueryRows<StoredKwSkillLoad>(
            sql.ToString(),
            parameters,
            static reader => new StoredKwSkillLoad(
                SpanId: reader.GetString(reader.GetOrdinal("span_id")),
                ToolName: reader.GetString(reader.GetOrdinal("tool_name")),
                McpServerName: ReadNullableString(reader, "server_name"),
                McpToolName: ReadNullableString(reader, "server_tool_name"),
                ReplyHead: ReadNullableString(reader, "reply_head"),
                StartedAtUtc: ParseTimestampRequired(reader.GetString(reader.GetOrdinal("started_at_utc")), "record.started_at_utc")));

        StringBuilder countSql = new();
        countSql.Append("SELECT COUNT(*) AS page_total FROM (SELECT tc.span_id FROM tool_call tc ");
        countSql.Append("JOIN record r ON r.span_id = tc.span_id ");
        countSql.Append("LEFT JOIN tool_call_mcp tcm ON tcm.span_id = tc.span_id ");
        AppendSessionKwLoadPredicate(countSql, key, parameters);
        countSql.Append(')');
        IReadOnlyList<int> countRows = QueryRows<int>(countSql.ToString(), parameters, static reader => reader.GetInt32(reader.GetOrdinal("page_total")));
        if (countRows.Count != 1)
        {
            throw new StoredDataException($"Expected 1 count row for the session KW skill load page, got {countRows.Count.ToString(CultureInfo.InvariantCulture)}.");
        }

        return new SessionKwSkillLoadPageResult(results, countRows[0]);
    }

    /// <summary>Appends the D17 load predicate selecting a session's KW skill load rows.</summary>
    /// <remarks>
    /// A load is a kw-docs <c>load_skill</c>: with an MCP split, the server name normalized (lower
    /// case, <c>_</c> to <c>-</c>) names kw-docs or kyber-weave (the same server's other registered
    /// name) and the split tool is <c>load_skill</c>; without
    /// one, the tool name is one of the unsplit D17 spellings. Another server's <c>load_skill</c>
    /// is not a load (D17), so the page and the total count loads only. The application
    /// recognizer keeps the final word on each row the page returns.
    /// </remarks>
    private protected static void AppendSessionKwLoadPredicate(StringBuilder sql, string sessionMarker, Dictionary<string, object?> parameters)
    {
        string unsplitNames = string.Join(", ", KwSkillLoadToolNames.Select(name => Bind(parameters, name)));
        sql.Append(CultureInfo.InvariantCulture, $"WHERE EXISTS (SELECT 1 FROM record_session_link rsl WHERE rsl.span_id = tc.span_id AND rsl.session_key = {sessionMarker}) ");
        sql.Append("AND (");
        sql.Append("((tcm.server_name IS NOT NULL OR tcm.server_tool_name IS NOT NULL) ");
        sql.Append("AND LOWER(REPLACE(COALESCE(tcm.server_name, ''), '_', '-')) IN ('kw-docs', 'kyber-weave') ");
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

        Dictionary<string, object?> parameters = new(StringComparer.Ordinal);
        string sessionMarker = Bind(parameters, sessionKey);
        StringBuilder ctes = new("WITH RECURSIVE ");
        AppendSessionSectionCtes(ctes, sessionMarker);
        ctes.Append("requested(request_span_id) AS (SELECT ");
        ctes.Append(string.Join(" UNION ALL SELECT ", requested.Select(id => Bind(parameters, id))));
        ctes.Append("), ");
        ctes.Append("subtree(root, span) AS (SELECT request_span_id, request_span_id FROM requested UNION SELECT s.root, ch.span_id FROM subtree s JOIN record_parent ch ON ch.parent_span_id = s.span AND ch.span_id IN (SELECT span_id FROM spans)), ");
        ctes.Append("candidate(root, span, ord) AS (SELECT root, span, 0 FROM subtree WHERE span = root UNION SELECT s.root, t.span_id, 1 + t.position FROM subtree s JOIN turn_entries t ON t.span_id = s.span WHERE t.span_id <> s.root) ");

        StringBuilder sql = new();
        sql.Append(ctes);
        sql.Append("SELECT c.root, cp.span_id, c.ord, cp.position, ");
        sql.Append("cp.bucket, cpt.part_text ");
        sql.Append("FROM candidate c ");
        sql.Append("JOIN content_part cp ON cp.span_id = c.span ");
        sql.Append("LEFT JOIN content_part_text cpt ON cpt.span_id = cp.span_id AND cpt.position = cp.position ");
        sql.Append("ORDER BY c.root ASC, c.ord ASC, cp.position ASC");

        List<(string Root, int Ord, string Span, int PartPosition, ContentBucket Bucket, string? PlainText)> partRows =
            [.. QueryRows<(string Root, int Ord, string Span, int PartPosition, ContentBucket Bucket, string? PlainText)>(
                sql.ToString(),
                parameters,
                static reader => (
                    reader.GetString(reader.GetOrdinal("root")),
                    reader.GetInt32(reader.GetOrdinal("ord")),
                    reader.GetString(reader.GetOrdinal("span_id")),
                    reader.GetInt32(reader.GetOrdinal("position")),
                    ParseContentBucket(reader.GetString(reader.GetOrdinal("bucket"))),
                    ReadNullableString(reader, "part_text")))];

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
    private protected static string? ExtractRequestPreviewFromRows(
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
    private protected static string? ExtractReaderPreview(
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
    private protected static void AppendSessionSectionCtes(StringBuilder sql, string keyExpr)
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
    /// <param name="parameters">Parameter values the count binds.</param>
    /// <returns>The set's row count.</returns>
    private protected int ReadSessionSectionTotal(StringBuilder ctes, string setName, string displayName, Dictionary<string, object?> parameters)
    {
        StringBuilder countSql = new();
        countSql.Append(ctes);
        countSql.Append(CultureInfo.InvariantCulture, $"SELECT COUNT(*) AS total FROM {setName}");
        IReadOnlyList<int> countRows = QueryRows<int>(countSql.ToString(), parameters, static reader => reader.GetInt32(reader.GetOrdinal("total")));
        if (countRows.Count != 1)
        {
            throw new StoredDataException($"Expected 1 count row for the {displayName} count, got {countRows.Count.ToString(CultureInfo.InvariantCulture)}.");
        }

        return countRows[0];
    }

    /// <summary>
    /// Appends the set of problem ids attached to one session — by <c>ingest_problem_session</c> or
    /// by one of the session's spans — as one parenthesized select.
    /// </summary>
    /// <param name="sql">Script the select is appended to.</param>
    /// <param name="key">Session key bound as one parameter marker.</param>
    private protected static void AppendSessionProblemAttachment(StringBuilder sql, string key)
    {
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
        // D11) and its response and request ids, so every record read hydrates the same join ids:
        // no trace, end time, token count, reported total, error or correction signal, tool
        // metadata, or content child row (decision D17).
        Dictionary<string, object?> parameters = new(StringComparer.Ordinal);
        string key = Bind(parameters, sessionKey);
        string sql =
            "SELECT r.span_id, r.harness_id, r.op, r.kind, r.name, r.started_at_utc, r.status, " +
            "rsl.session_key, rp.parent_span_id, rm.model_id, rrd.duration_ms, rrc.amount, rrc.currency, " +
            "rdcw.declared_context_window, rrid.response_id, rqid.request_id, " +
            "src.source_kind, src.harness_id AS source_harness_id, src.locator " +
            "FROM record r " +
            RecordSourceJoin +
            RecordJoinIdJoin +
            "LEFT JOIN record_session_link rsl ON rsl.span_id = r.span_id " +
            "LEFT JOIN record_parent rp ON rp.span_id = r.span_id " +
            "LEFT JOIN record_model rm ON rm.span_id = r.span_id " +
            "LEFT JOIN record_reported_duration rrd ON rrd.span_id = r.span_id " +
            "LEFT JOIN record_reported_cost rrc ON rrc.span_id = r.span_id " +
            "LEFT JOIN record_declared_context_window rdcw ON rdcw.span_id = r.span_id " +
            $"WHERE rsl.session_key = {key} ORDER BY r.rowid ASC";

        return QueryRows<CollectedRecord>(
            sql,
            parameters,
            static reader =>
            {
                double? costAmount = ReadNullableDouble(reader, "amount");
                string? costCurrency = ReadNullableString(reader, "currency");
                CollectedCost? reportedCost = costAmount.HasValue && costCurrency is not null
                    ? new CollectedCost(costAmount.Value, costCurrency)
                    : costAmount.HasValue || costCurrency is not null
                        ? throw new StoredDataException("Partial record_reported_cost row returned.")
                        : null;
                SourceKind sourceKind = ParseSourceKind(reader.GetString(reader.GetOrdinal("source_kind")));
                string sourceName = sourceKind == SourceKind.File
                    ? CanonSynth.FileSourcePrefix + reader.GetString(reader.GetOrdinal("source_harness_id"))
                    : reader.GetString(reader.GetOrdinal("locator"));

                return new CollectedRecord(
                    reader.GetString(reader.GetOrdinal("span_id")),
                    reader.GetString(reader.GetOrdinal("harness_id")),
                    ParseRecordOp(reader.GetString(reader.GetOrdinal("op"))),
                    ParseRecordKind(reader.GetString(reader.GetOrdinal("kind"))),
                    reader.GetString(reader.GetOrdinal("name")),
                    ParseTimestampRequired(reader.GetString(reader.GetOrdinal("started_at_utc")), "record.started_at_utc"),
                    ParseRecordStatus(reader.GetString(reader.GetOrdinal("status"))),
                    ReadNullableString(reader, "session_key"),
                    ReadNullableString(reader, "parent_span_id"),
                    TraceId: null,
                    ReadNullableString(reader, "model_id"),
                    EndedAt: null,
                    ReadNullableInt64(reader, "duration_ms"),
                    Tokens: [],
                    reportedCost,
                    Parts: [],
                    Tool: null,
                    Agent: null,
                    SkillsInvoked: [],
                    AgentInstanceId: null,
                    ToolDefinitions: [],
                    new CollectedRecordSignals(ErrorMessage: null, EditFailures: null, CorrectionRule: null, ReadNullableInt64(reader, "declared_context_window")),
                    ReportedInputTokens: null,
                    ReportedOutputTokens: null,
                    SourceName: sourceName,
                    ResponseId: ReadNullableString(reader, "response_id"),
                    RequestId: ReadNullableString(reader, "request_id"));
            });
    }
}
