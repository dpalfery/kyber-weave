using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using KyberDash.Contracts.Repositories;
using KyberDash.Data.Collector;
using KyberDash.Domain;

namespace KyberDash.Application;

/// <summary>
/// Incremental session projection, same-turn twin deduplication, turn repricing, and
/// subagent/finding orchestration over the relational store contracts.
/// </summary>
/// <remarks>
/// <para>
/// The primary constructor projects sessions typed: it reads records only through
/// <see cref="IRelationalStoreReader"/>, counts tokens through <see cref="IOperationalStore"/>
/// (decision D16), and writes <see cref="StoredSessionProjection"/> rows, typed context and
/// schema children (with exclusive <c>_unmeasurable</c> rows), and timeline marks through
/// <see cref="IProjectionStore"/> — no JSON payload column anywhere.
/// </para>
/// <para>
/// A transitional constructor keeps the <see cref="ICanonStoreRepository"/> path for callers not
/// yet migrated (decision D4's dual-registration pattern); T45/T53 remove it once the last legacy
/// caller moves over.
/// </para>
/// </remarks>
public partial class CanonSessionProjectorEngine
{
    /// <summary>Maximum timestamp skew (ms) for exact-counter or Cursor overlap twin collapsing.</summary>
    public const long TwinTurnMaxSkewMs = 60_000;

    private const int PromptPreviewLength = 200;
    private const string FileSourcePrefix = "codeburn/";
    private const string DroidHarness = "droid";
    private const string DroidPluginSource = "kyberdash-factory-plugin";
    private const string SessionLabelKey = "kyber.session.label";
    private const string ProjectionRecordsMarkerKey = "projection_records_marker";
    private const string ProjectionSchemaVersionKey = "projection_schema_version";
    private const string RelationalSourceName = "relational";
    private const string TimestampFormat = "yyyy-MM-dd'T'HH:mm:ss.fff'Z'";
    private const string NoMessageStructureReason = "no_message_structure";
    private const string NoToolDefinitionSchemaReason = "The source did not provide tool-definition schema data.";
    private const string DefaultContextLimitSource = "default";
    private const int TypedDefinitionOrderBase = 1_000;

    private static readonly string[] AgentNameKeys = ["gen_ai.agent.name", "agent.name"];
    private static readonly string[] RepoKeys = ["vcs.repository.name", "repo"];
    private static readonly string[] BranchKeys = ["vcs.ref.head.name", "branch"];
    private static readonly string[] ModelKeys = ["gen_ai.response.model", "gen_ai.request.model", "model"];
    private static readonly string[] ParentSessionKeys = ["parent_session", "parent_session_id", "gen_ai.parent_session_id", "parentSession"];

    private static readonly HashSet<string> SideAgentNames = new(StringComparer.Ordinal)
    {
        "backgroundTodoAgent",
        "progressMessages",
        "title",
        "gitCommitMessageGenerator",
    };

    private static readonly JsonSerializerOptions ProjectionJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly IRelationalStoreReader? reader;
    private readonly IProjectionStore? projections;
    private readonly IOperationalStore? operationalStore;
    private readonly ICanonStoreRepository? legacyStore;
    private readonly IPricingRepository pricing;

    /// <summary>
    /// Initializes a relational <see cref="CanonSessionProjectorEngine"/> projecting typed session
    /// rows from the normalized relational store.
    /// </summary>
    /// <param name="reader">Relational reader supplying sessions, records, and typed children.</param>
    /// <param name="projections">Typed projection store receiving session, context, schema, timeline, and finding rows.</param>
    /// <param name="operationalStore">Operational store backing the token counter and projection watermarks.</param>
    /// <param name="pricing">Model rate-table repository used when resolving context windows.</param>
    public CanonSessionProjectorEngine(
        IRelationalStoreReader reader,
        IProjectionStore projections,
        IOperationalStore operationalStore,
        IPricingRepository pricing)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(projections);
        ArgumentNullException.ThrowIfNull(operationalStore);
        ArgumentNullException.ThrowIfNull(pricing);

        this.reader = reader;
        this.projections = projections;
        this.operationalStore = operationalStore;
        this.pricing = pricing;
    }

    /// <summary>
    /// Initializes a transitional <see cref="CanonSessionProjectorEngine"/> over
    /// <see cref="ICanonStoreRepository"/> for callers not yet migrated to the relational contracts.
    /// </summary>
    /// <param name="store">Canonical store repository.</param>
    /// <param name="pricing">Model rate-table repository used when repricing turns.</param>
    public CanonSessionProjectorEngine(ICanonStoreRepository store, IPricingRepository pricing)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(pricing);

        legacyStore = store;
        this.pricing = pricing;
    }

    /// <summary>Canonical store repository backing this engine; <see langword="null"/> on the relational path.</summary>
    public ICanonStoreRepository? Store => legacyStore;

    [GeneratedRegex(@"^invoke_agent(?:[ :]|$)", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex InvokeAgentNameRegex();

    /// <summary>
    /// Collapses same-turn twin observations across ID joins, exact-counter clusters, and
    /// evidenced Cursor overlap shares without mutating input records.
    /// </summary>
    /// <param name="records">Candidate canonical records.</param>
    /// <param name="shareKey">Fallback session key used when a record has no <see cref="CanonicalRecord.SessionId"/>.</param>
    /// <returns>Deduplicated canonical records preserving input order.</returns>
    public static IReadOnlyList<CanonicalRecord> DedupeTwinTurns(
        IReadOnlyList<CanonicalRecord> records,
        string? shareKey = null)
    {
        ArgumentNullException.ThrowIfNull(records);
        if (records.Count == 0)
        {
            return [];
        }

        HashSet<CanonicalRecord> dropped = new(ReferenceEqualityComparer.Instance);
        Dictionary<CanonicalRecord, CanonicalRecord> transplant = new(ReferenceEqualityComparer.Instance);
        HashSet<CanonicalRecord> idJoined = CollapseIdJoins(records, shareKey, dropped, transplant);

        Dictionary<string, List<CanonicalRecord>> byCounter = new(StringComparer.Ordinal);
        foreach (CanonicalRecord record in records)
        {
            if (!string.Equals(record.Op, "llm.invoke", StringComparison.Ordinal))
            {
                continue;
            }

            if (idJoined.Contains(record))
            {
                continue;
            }

            if (IsUnreportedCounters(record.Tokens))
            {
                continue;
            }

            string key = CounterKey(record, shareKey);
            if (!byCounter.TryGetValue(key, out List<CanonicalRecord>? list))
            {
                list = [];
                byCounter[key] = list;
            }

            list.Add(record);
        }

        foreach (List<CanonicalRecord> turns in byCounter.Values)
        {
            turns.Sort(CompareRecordTimestamps);
            List<CanonicalRecord> cluster = [];
            foreach (CanonicalRecord record in turns)
            {
                if (cluster.Count > 0 && RecordTimestampMs(record) - RecordTimestampMs(cluster[0]) > TwinTurnMaxSkewMs)
                {
                    CollapseCluster(cluster, dropped, transplant);
                    cluster.Clear();
                }

                cluster.Add(record);
            }

            CollapseCluster(cluster, dropped, transplant);
        }

        Dictionary<CanonicalRecord, TokenUsage> tokenOverride = new(ReferenceEqualityComparer.Instance);
        CollapseOverlapFileJoins(records, shareKey, dropped, transplant, tokenOverride);

        List<CanonicalRecord> output = new(records.Count);
        foreach (CanonicalRecord record in records)
        {
            if (dropped.Contains(record))
            {
                continue;
            }

            TokenUsage tokens = tokenOverride.GetValueOrDefault(record, record.Tokens);
            if (!transplant.TryGetValue(record, out CanonicalRecord? donor))
            {
                output.Add(ReferenceEquals(tokens, record.Tokens) ? record : record with { Tokens = tokens });
                continue;
            }

            if (!IsFileSource(record.Source))
            {
                CanonicalRecord mergedOtel = MergeOtelFirst(record, donor);
                output.Add(ReferenceEquals(tokens, record.Tokens) ? mergedOtel : mergedOtel with { Tokens = tokens });
                continue;
            }

            IReadOnlyList<ContentPart> keeperParts = record.Parts ?? [];
            HashSet<string> seen = new(StringComparer.Ordinal);
            foreach (ContentPart part in keeperParts)
            {
                seen.Add($"{part.Part}\u0000{part.Text}");
            }

            List<ContentPart> extraParts = [];
            foreach (ContentPart part in donor.Parts ?? [])
            {
                if (seen.Add($"{part.Part}\u0000{part.Text}"))
                {
                    extraParts.Add(part);
                }
            }

            List<ContentPart> newParts = [.. keeperParts, .. extraParts];
            Dictionary<string, string> mergedContent = MergeDictionaries(donor.Content, record.Content);
            if (newParts.Count > 0)
            {
                mergedContent = MergeDictionaries(mergedContent, CanonValidation.ContentFromParts(newParts));
            }

            bool keepParts = (donor.Parts is { Count: > 0 }) || extraParts.Count > 0 || (record.Parts is { Count: > 0 });
            output.Add(record with
            {
                Tokens = tokens,
                Parts = keepParts ? newParts : record.Parts,
                Content = mergedContent,
            });
        }

        return output;
    }

    /// <summary>
    /// Derives a complete <see cref="SessionSummaryRow"/> using this engine's cached token counter.
    /// </summary>
    /// <param name="sessionId">Canonical session identifier.</param>
    /// <param name="records">Session records in timestamp order.</param>
    /// <returns>Derived session summary row.</returns>
    public SessionSummaryRow BuildSessionSummary(string sessionId, IReadOnlyList<CanonicalRecord> records)
    {
        _ = pricing.LoadPricingTables();
        CachedTokenCounter counter = CreateTokenCounter();
        SessionSummaryRow row = BuildSessionSummary(sessionId, records, counter.Count);
        counter.Flush();
        return row;
    }

    /// <summary>
    /// Builds the engine's cached token counter over <see cref="IOperationalStore"/> on the
    /// relational path (decision D16); the transitional legacy constructor keeps the
    /// <see cref="ICanonStoreRepository"/>-backed counter until T45 removes it.
    /// </summary>
    /// <returns>Cached token counter bound to the engine's operational or legacy store.</returns>
    private CachedTokenCounter CreateTokenCounter() => operationalStore is not null
        ? new CachedTokenCounter(operationalStore, CanonTokenCache.ApproximateTokenizerName)
        : new CachedTokenCounter(legacyStore!, CanonTokenCache.ApproximateTokenizerName);

    /// <summary>
    /// Derives a complete <see cref="SessionSummaryRow"/> (including <c>summary_json</c>,
    /// context analysis, schema ranking, timeline without raw attributes, and turns) from
    /// one session's records.
    /// </summary>
    /// <param name="sessionId">Canonical session identifier.</param>
    /// <param name="records">Session records.</param>
    /// <param name="countTokens">Token counting callback.</param>
    /// <param name="isSubagent">Whether this session represents a delegated subagent execution.</param>
    /// <param name="parentSession">Parent session identifier when delegated.</param>
    /// <param name="agentName">Delegated agent name when available.</param>
    /// <param name="label">Display label override.</param>
    /// <param name="includedInParent">Whether this subagent's spend is included in its parent.</param>
    /// <returns>Projected <see cref="SessionSummaryRow"/>.</returns>
    public static SessionSummaryRow BuildSessionSummary(
        string sessionId,
        IReadOnlyList<CanonicalRecord> records,
        Func<string, long> countTokens,
        bool isSubagent = false,
        string? parentSession = null,
        string? agentName = null,
        string? label = null,
        bool includedInParent = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentNullException.ThrowIfNull(records);
        ArgumentNullException.ThrowIfNull(countTokens);
        if (records.Count == 0)
        {
            throw new ArgumentException("At least one record is required to build a session summary.", nameof(records));
        }

        DroidSessionSplit split = SplitDroidSessionRecords(records);
        bool paired = split.TurnRecords.Count > 0 && split.CounterRecords.Count > 0;
        List<CanonicalRecord> turnRecords = paired ? TurnsOf(split.TurnRecords) : TurnsOf(records);
        IReadOnlyList<CanonicalRecord> structureRecords = paired ? split.TurnRecords : records;
        List<CanonicalRecord> counterRecords = paired
            ? split.CounterRecords.Where(static r => !string.Equals(r.Op, "tool.invoke", StringComparison.Ordinal)).ToList()
            : [];

        CanonicalRecord first = records[0];
        string harness = CanonPricing.NormalizeHarnessName(first.Harness);

        List<CanonicalRecord> mainThreadRecords = turnRecords.Where(static r => !IsSideAgentTurn(r)).ToList();
        List<CanonicalRecord> contextRecords = mainThreadRecords.Count > 0 ? mainThreadRecords : turnRecords;
        List<CanonicalRecord> measuredTurns = contextRecords.Where(static r => CanonPricing.MeasuredInput(r.Tokens) > 0).ToList();
        int unmeasuredTurns = contextRecords.Count - measuredTurns.Count;

        long? counterInput = counterRecords.Count > 0
            ? counterRecords.Sum(static r => CanonPricing.MeasuredInput(r.Tokens))
            : null;

        List<ContextPart> lastSystem = [];
        List<ContextTurn> contextTurns = new(measuredTurns.Count);
        foreach (CanonicalRecord record in measuredTurns)
        {
            List<ContextPart> parts = PartsOf(record);
            List<ContextPart> ownSystem = parts.Where(static p => string.Equals(p.Part, "system_prompt", StringComparison.Ordinal)).ToList();
            if (ownSystem.Count > 0)
            {
                lastSystem = ownSystem;
            }

            IReadOnlyList<ContextPart> effective = ownSystem.Count > 0 ? parts : [.. lastSystem, .. parts];
            contextTurns.Add(new ContextTurn(
                Parts: effective,
                InputTokens: CanonPricing.MeasuredInput(record.Tokens),
                FreshInput: record.Tokens.FreshInput));
        }

        IReadOnlyList<CanonicalRecord> windowRecords = contextRecords.Count > 0 ? contextRecords : records;
        ContextWindowResult window = CanonPricing.ContextLimitOf(windowRecords);
        long analysisLimit = window.ContextLimit > 0 ? window.ContextLimit : CanonPricing.DefaultContextLimit;

        Dictionary<string, JsonElement>? measurability = MergeMeasurability(records);
        IReadOnlyList<CanonicalRecord> totalRecords = counterRecords.Count > 0 ? counterRecords : turnRecords;
        Dictionary<string, JsonElement>? totalMeasurability = MergeMeasurability(totalRecords);
        HashSet<string> carriedBuckets = CarriedBucketsOf(records);
        IReadOnlyDictionary<string, JsonElement>? compMeasurability = CompositionDeclaration(measurability, carriedBuckets);

        ContextAnalysis context = CanonContextAnalysis.AnalyzeContext(
            contextTurns,
            new ContextCountOptions(
                ContextLimit: analysisLimit,
                ContextLimitSource: window.ContextLimitSource,
                CountTokens: countTokens,
                Measurability: compMeasurability));

        List<ToolDefinition> definitions = ToolDefinitionsOf(records, countTokens);
        IReadOnlyList<CanonicalRecord> toolSourceRecords = paired && split.FileToolRecords.Count > 0
            ? split.FileToolRecords
            : structureRecords;
        SchemaCostAnalysis schema = CanonContextAnalysis.RankSchemas(
            definitions,
            turnRecords.Count,
            InvocationsOf(toolSourceRecords),
            measurability: measurability);

        List<CanonicalRecord> toolRecords = toolSourceRecords.Where(static r => string.Equals(r.Op, "tool.invoke", StringComparison.Ordinal)).ToList();
        Dictionary<string, int> invocationCounts = new(StringComparer.Ordinal);
        Dictionary<string, int> errorCounts = new(StringComparer.Ordinal);
        foreach (CanonicalRecord toolRecord in toolRecords)
        {
            string toolName = (AttributeOf(toolRecord, ["gen_ai.tool.name", "tool.name", "tool_name"]) ?? toolRecord.Name).Trim();
            if (toolName.Length == 0 || string.Equals(toolName, "tool.invoke", StringComparison.Ordinal))
            {
                continue;
            }

            invocationCounts[toolName] = invocationCounts.GetValueOrDefault(toolName) + 1;
            if (IsToolError(toolRecord))
            {
                errorCounts[toolName] = errorCounts.GetValueOrDefault(toolName) + 1;
            }
        }

        Dictionary<string, ToolDefinition> definitionsByName = new(StringComparer.Ordinal);
        foreach (ToolDefinition def in definitions)
        {
            definitionsByName.TryAdd(def.Name, def);
        }

        List<RankedTool> rankedTools = schema.Measurable
            ? [.. schema.Ranked]
            : definitions.Select(d => new RankedTool(d.Name, d.Server, d.Tokens * (d.TurnsResident ?? turnRecords.Count), false)).ToList();

        List<Dictionary<string, object?>> toolsPayload = [];
        HashSet<string> seenToolNames = new(StringComparer.Ordinal);
        foreach (RankedTool ranked in rankedTools)
        {
            seenToolNames.Add(ranked.Name);
            definitionsByName.TryGetValue(ranked.Name, out ToolDefinition? def);
            long schemaTokens = def?.Tokens ?? 0;
            int turnsResident = def?.TurnsResident ?? turnRecords.Count;
            int inv = invocationCounts.GetValueOrDefault(ranked.Name);
            int errs = errorCounts.GetValueOrDefault(ranked.Name);
            Dictionary<string, object?> entry = new(StringComparer.Ordinal)
            {
                ["name"] = ranked.Name,
                ["schema_tokens"] = schemaTokens,
                ["invocations"] = inv,
                ["turns_resident"] = turnsResident,
                ["errors"] = errs,
                ["total_schema_cost"] = ranked.Cost,
            };
            if (def?.Server is not null)
            {
                entry["server"] = def.Server;
            }

            toolsPayload.Add(entry);
        }

        foreach (KeyValuePair<string, int> kv in invocationCounts)
        {
            if (!seenToolNames.Add(kv.Key))
            {
                continue;
            }

            toolsPayload.Add(new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["name"] = kv.Key,
                ["schema_tokens"] = 0L,
                ["invocations"] = kv.Value,
                ["turns_resident"] = 0,
                ["errors"] = errorCounts.GetValueOrDefault(kv.Key),
            });
        }

        List<Dictionary<string, object?>> serversPayload = [];
        if (schema.Measurable)
        {
            foreach (string server in schema.ByServer.Keys)
            {
                List<RankedTool> serverTools = rankedTools.Where(t => string.Equals(t.Server, server, StringComparison.Ordinal)).ToList();
                serversPayload.Add(new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["server"] = server,
                    ["is_mcp"] = true,
                    ["tools"] = serverTools.Count,
                    ["schema_tokens"] = serverTools.Sum(t => definitionsByName.GetValueOrDefault(t.Name)?.Tokens ?? 0),
                    ["invocations"] = serverTools.Sum(t => invocationCounts.GetValueOrDefault(t.Name)),
                    ["unused_tools"] = serverTools.Count(t => invocationCounts.GetValueOrDefault(t.Name) == 0),
                    ["unused_cost"] = serverTools.Where(t => invocationCounts.GetValueOrDefault(t.Name) == 0).Sum(t => t.Cost),
                });
            }
        }

        List<CostBlock> costBlocks = [.. turnRecords.Select(static r => r.Cost), .. counterRecords.Select(static r => r.Cost)];
        List<CostBlock> pricedBlocks = costBlocks.Where(static b => b.Value.HasValue && double.IsFinite(b.Value.Value)).ToList();
        CostTotalResult costResult = CanonPricing.SumCosts(paired && pricedBlocks.Count > 0 ? pricedBlocks : costBlocks);
        CostBasisMismatch? costBasisMismatch = CanonPricing.CostBasisMismatchOf(costBlocks, costResult);

        long totalInput = 0;
        long totalOutput = 0;
        long totalCacheRead = 0;
        long totalCacheCreation = 0;
        foreach (CanonicalRecord r in totalRecords)
        {
            totalInput += CanonPricing.MeasuredInput(r.Tokens);
            totalOutput += r.Tokens.Output;
            totalCacheRead += r.Tokens.CacheRead;
            totalCacheCreation += r.Tokens.CacheCreation;
        }

        List<string> turnModels = ModelsOf(turnRecords);
        List<string> models = turnModels.Count > 0 ? turnModels : ModelsOf(counterRecords);
        (string started, string ended) = TimestampBounds(records);

        int? toolCalls = null;
        List<string>? toolsInvoked = null;
        if (toolRecords.Count > 0)
        {
            bool lowerBound = toolRecords.Any(static r => IsNotMeasurableElement(r.Measurability, "tool_calls"));
            if (!lowerBound)
            {
                toolCalls = toolRecords.Count;
            }

            toolsInvoked = [.. InvocationsOf(toolSourceRecords).Distinct(StringComparer.Ordinal)];
        }
        else if (IsMeasuredOrDerived(measurability, "tool_calls") || CanonPricing.SurveyFamily(harness) == "claude-code")
        {
            toolCalls = 0;
            toolsInvoked = [];
        }

        List<string> offeredTools = [];
        HashSet<string> seenOfferedTools = new(StringComparer.Ordinal);
        bool hasDefParts = false;
        foreach (CanonicalRecord record in records)
        {
            foreach (ToolDefinition def in ToolDefinitionsOf([record], countTokens))
            {
                hasDefParts = true;
                if (seenOfferedTools.Add(def.Name))
                {
                    offeredTools.Add(def.Name);
                }
            }

            if (record.Parts is not null && record.Parts.Any(static p => string.Equals(p.Part, "tool_definitions", StringComparison.Ordinal) || string.Equals(p.Part, "tool_definition", StringComparison.Ordinal)))
            {
                hasDefParts = true;
            }
        }

        List<CanonicalRecord> requestRoots = structureRecords.Where(static r => r.ParentSpanId is null).ToList();
        Dictionary<string, List<CanonicalRecord>> turnsByRoot = TurnsByRequestRoot(structureRecords, turnRecords);
        bool isNested = requestRoots.Count > 0 && HasNesting(structureRecords);
        IReadOnlyList<CanonicalRecord> durationRecords = paired ? counterRecords : isNested ? requestRoots : structureRecords;
        long totalDurationMs = durationRecords.Sum(static r => r.DurationMs);

        TimelineNode timeline = CanonContextAnalysis.BuildTimeline(structureRecords);
        IReadOnlyList<SubagentSession> subagents = CanonContextAnalysis.SubagentSessions(timeline);
        CostBlock auxiliary = CanonContextAnalysis.AuxiliarySpend(timeline);

        string? resolvedAgentName = agentName ?? AttributeOf(first, AgentNameKeys);
        string resolvedLabel = SessionLabelOf(records, label ?? first.Name, harness, sessionId);
        string? repo = AttributeOf(first, RepoKeys);
        string? branch = AttributeOf(first, BranchKeys);

        string? parentAttr = AttributeOf(first, ParentSessionKeys);
        bool resolvedIsSubagent = isSubagent || parentAttr is not null || AttributeOf(first, ["gen_ai.is_subagent", "is_subagent"]) is not null;
        string? resolvedParent = parentSession ?? parentAttr;

        object? summaryTotalInput = UnavailableOrValue(totalMeasurability, ["token_usage"], totalInput);
        object? summaryTotalOutput = UnavailableOrValue(totalMeasurability, ["output", "output_tokens"], totalOutput);
        object? summaryTotalCacheRead = UnavailableOrValue(totalMeasurability, ["cache_read"], totalCacheRead);
        object? summaryTotalCacheCreation = UnavailableOrValue(totalMeasurability, ["cache_creation"], totalCacheCreation);

        Dictionary<string, object?> summaryDict = new(StringComparer.Ordinal)
        {
            ["turn_count"] = turnRecords.Count,
            ["request_count"] = requestRoots.Count,
            ["total_input"] = summaryTotalInput,
            ["total_output"] = summaryTotalOutput,
            ["total_cache_read"] = summaryTotalCacheRead,
            ["total_cache_creation"] = summaryTotalCacheCreation,
            ["duration_ms"] = totalDurationMs,
            ["models"] = models,
            ["cost"] = costResult.Ok && costResult.Total is not null
                ? costResult.Total
                : new CostBlock(CostBasis.Unknown, CostStatus.NoRate),
        };

        if (summaryTotalInput is long numIn && numIn > 0 && summaryTotalCacheRead is long numCr)
        {
            summaryDict["cache_hit_ratio"] = (double)numCr / numIn;
        }

        if (summaryTotalInput is long && totalRecords.Count > 0 && summaryTotalInput is long numIn2 && numIn2 > 0 && summaryTotalCacheCreation is long && turnRecords.Count > 0)
        {
            summaryDict["cache_creation_coverage"] = totalRecords.Count(static r => r.Tokens.CacheCreation > 0);
        }

        if (toolCalls.HasValue)
        {
            summaryDict["tool_calls"] = toolCalls.Value;
        }

        if (toolsInvoked is not null)
        {
            summaryDict["tools_invoked"] = toolsInvoked;
        }

        if (hasDefParts)
        {
            summaryDict["tools_offered"] = offeredTools;
        }
        else if (TryUnavailable(measurability, "tool_definitions", out JsonElement toolDefUnavail))
        {
            summaryDict["tools_offered"] = toolDefUnavail;
        }

        if (includedInParent)
        {
            summaryDict["included_in_parent"] = true;
        }

        Dictionary<string, object?> contextDict = SerializeContextPayload(
            context,
            window.ContextLimitSource,
            measuredTurns,
            counterInput,
            unmeasuredTurns,
            totalMeasurability);

        List<Dictionary<string, object?>> turnEntries = [];
        for (int i = 0; i < turnRecords.Count; i++)
        {
            CanonicalRecord tr = turnRecords[i];
            turnEntries.Add(new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["index"] = i,
                ["spanId"] = tr.SpanId,
                ["timestamp"] = tr.Timestamp,
                ["model"] = AttributeOf(tr, ModelKeys),
                ["input"] = CanonPricing.MeasuredInput(tr.Tokens),
                ["output"] = tr.Tokens.Output,
                ["fresh"] = tr.Tokens.FreshInput,
                ["cache_read"] = tr.Tokens.CacheRead,
                ["cache_creation"] = tr.Tokens.CacheCreation,
                ["reasoning"] = tr.Tokens.Reasoning,
            });
        }

        List<Dictionary<string, object?>> requestEntries = [];
        foreach (CanonicalRecord req in requestRoots)
        {
            turnsByRoot.TryGetValue(req.SpanId, out List<CanonicalRecord>? ownedTurns);
            string? reqModel = AttributeOf(req, ModelKeys)
                ?? ownedTurns?.Select(static t => AttributeOf(t, ModelKeys)).FirstOrDefault(static m => m is not null);
            object? promptValue = PromptPreviewOf(req);
            if (promptValue is null && TryUnavailable(req.Measurability, "user_prompt", out JsonElement promptUnavail))
            {
                promptValue = promptUnavail;
            }

            requestEntries.Add(new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["request"] = req.SpanId,
                ["timestamp"] = req.Timestamp,
                ["turns"] = ownedTurns?.Count ?? 0,
                ["model"] = reqModel,
                ["prompt"] = promptValue,
            });
        }

        List<Dictionary<string, object?>> reconciliation = turnRecords.Select(static r =>
        {
            long measuredIn = CanonPricing.MeasuredInput(r.Tokens);
            return new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["request"] = r.SpanId,
                ["root_input"] = r.Tokens.ReportedInput,
                ["sum_chat_input"] = measuredIn,
                ["input_match"] = r.Tokens.ReportedInput == measuredIn,
                ["root_output"] = r.Tokens.ReportedOutput,
                ["sum_chat_output"] = r.Tokens.Output,
                ["output_match"] = r.Tokens.ReportedOutput == r.Tokens.Output,
            };
        }).ToList();

        object timelinePayload = TryUnavailable(measurability, "execution_structure", out JsonElement timelineUnavail)
            ? timelineUnavail
            : timeline.Children.Select(WithoutAttributes).ToList();

        object schemaPayload = schema.Measurable
            ? new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["measurable"] = true,
                ["byServer"] = schema.ByServer,
                ["neverInvoked"] = schema.NeverInvoked,
                ["unusedRange"] = schema.UnusedRange,
                ["turns"] = schema.Turns,
            }
            : TryUnavailable(measurability, "schema_cost", out JsonElement schemaUnavail)
                ? schemaUnavail
                : new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["availability"] = "not_measurable",
                    ["reason"] = "The source did not provide tool-definition schema data.",
                };

        Problem[] problems = costResult.Ok || costResult.Problem is null ? [] : [costResult.Problem];

        Dictionary<string, object?> payloadDict = new(StringComparer.Ordinal)
        {
            ["id"] = sessionId,
            ["session_id"] = sessionId,
            ["harness"] = harness,
            ["label"] = resolvedLabel,
            ["agent_name"] = resolvedAgentName,
            ["repo"] = repo,
            ["branch"] = branch,
            ["span_count"] = records.Count,
            ["summary"] = summaryDict,
            ["context"] = contextDict,
            ["tools"] = toolsPayload,
            ["schema"] = schemaPayload,
            ["turns"] = turnEntries,
            ["timeline"] = timelinePayload,
            ["requests"] = requestEntries,
            ["servers"] = serversPayload,
            ["coverage"] = new Dictionary<string, int>(StringComparer.Ordinal)
            {
                ["schema"] = schema.Measurable ? 1 : 0,
                ["context"] = context.Measurable ? 1 : 0,
            },
            ["problems"] = problems,
            ["costBasisMismatch"] = costBasisMismatch,
            ["reconciliation"] = reconciliation,
            ["subagents"] = subagents,
            ["auxiliary"] = auxiliary,
            ["measurability"] = measurability ?? new Dictionary<string, JsonElement>(StringComparer.Ordinal),
        };

        string payloadJson = JsonSerializer.Serialize(payloadDict, ProjectionJsonOptions);
        JsonElement payloadElement = JsonSerializer.Deserialize<JsonElement>(payloadJson);
        string summaryJson = JsonSerializer.Serialize(summaryDict, ProjectionJsonOptions);

        return new SessionSummaryRow(
            SessionId: sessionId,
            Harness: harness,
            Label: resolvedLabel,
            IsSubagent: resolvedIsSubagent,
            ParentSession: resolvedParent,
            AgentName: resolvedAgentName,
            Repo: repo,
            Branch: branch,
            Started: started,
            Ended: ended,
            SummaryJson: summaryJson,
            ProblemsCount: problems.Length,
            ContextLimitSource: window.ContextLimitSource,
            Payload: payloadElement);
    }

    /// <summary>
    /// Returns <see langword="true"/> when the backing store already holds a current session
    /// projection matching its <c>records</c> write marker, schema version, and detector version.
    /// </summary>
    /// <returns><see langword="true"/> when stored projections match the current write marker and versions.</returns>
    public bool IsProjectionCurrent()
    {
        return legacyStore is not null ? IsLegacyProjectionCurrent() : IsRelationalProjectionCurrent();
    }

    private bool IsRelationalProjectionCurrent()
    {
        long currentMarker = operationalStore!.GetWriteMarker();
        ProjectionStateView? state = operationalStore.GetProjectionState();
        if (state is null)
        {
            return currentMarker == 0 && reader!.CountRecords() == 0 && projections!.ListSessionProjections().Count == 0;
        }

        if (state.RecordsMarker != currentMarker || !HasCurrentProjectionEnvironment())
        {
            return false;
        }

        return reader!.CountRecords() == 0 || projections!.ListSessionProjections().Count > 0;
    }

    private bool IsLegacyProjectionCurrent()
    {
        long currentMarker = legacyStore!.RecordsWriteMarker();
        string? prevMarkerRaw = legacyStore.GetMetadata(ProjectionRecordsMarkerKey);
        if (!long.TryParse(prevMarkerRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out long prevMarker))
        {
            return currentMarker == 0 && legacyStore.CountRecords() == 0 && legacyStore.ListSessions().Count == 0;
        }

        if (prevMarker != currentMarker || !HasCurrentProjectionEnvironment())
        {
            return false;
        }

        return legacyStore.CountRecords() == 0 || legacyStore.ListSessions().Count > 0;
    }

    /// <summary>
    /// Runs an incremental session projection pass over the store, rebuilding
    /// only session keys dirtied since the previous pass (or all keys on a full/first pass).
    /// </summary>
    /// <returns>Summary of built, skipped, and pruned sessions plus emitted finding count.</returns>
    public ProjectionPassReport ProjectIncremental()
    {
        return legacyStore is not null ? ProjectIncrementalLegacy() : ProjectIncrementalRelational();
    }

    private ProjectionPassReport ProjectIncrementalLegacy()
    {
        ICanonStoreRepository store = legacyStore!;
        _ = pricing.LoadPricingTables();

        string? prevMarkerRaw = store.GetMetadata(ProjectionRecordsMarkerKey);
        bool hasPrevMarker = long.TryParse(prevMarkerRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out long prevMarker);
        long currentMarker = store.RecordsWriteMarker();
        DirtyProjectionState dirtyState = store.GetDirtyProjectionState();
        IReadOnlyDictionary<string, IReadOnlyList<string>>? existingMap = store.GetProjectedSessionKeyMap();
        bool environmentCurrent = HasCurrentProjectionEnvironment();

        bool canIncremental = environmentCurrent
            && hasPrevMarker
            && dirtyState.FromSeq == prevMarker
            && dirtyState.ToSeq == currentMarker
            && !dirtyState.IsWildcard
            && dirtyState.Keys is not null
            && existingMap is not null;

        HashSet<string>? dirtySet = canIncremental
            ? new HashSet<string>(dirtyState.Keys!, StringComparer.Ordinal)
            : null;
        HashSet<string>? existingBuiltSet = canIncremental
            ? new HashSet<string>(store.GetBuiltSessionIds(), StringComparer.Ordinal)
            : null;

        CachedTokenCounter counter = new(legacyStore!, CanonTokenCache.ApproximateTokenizerName);
        SessionIdentities identities = new(store.GetSessionKeyHarnesses());
        IReadOnlyList<string> sessionKeys = store.GetDistinctSessionKeys();

        int builtCount = 0;
        int skippedCount = 0;
        int findingsCount = 0;
        List<string> rebuiltKeys = [];
        List<string> skippedKeys = [];
        HashSet<string> builtSessionIds = new(StringComparer.Ordinal);
        Dictionary<string, IReadOnlyList<string>> sessionIdsByKey = new(StringComparer.Ordinal);

        foreach (string key in sessionKeys)
        {
            if (canIncremental
                && dirtySet is not null
                && !dirtySet.Contains(key)
                && existingMap!.TryGetValue(key, out IReadOnlyList<string>? priorIds)
                && priorIds.All(existingBuiltSet!.Contains))
            {
                foreach (string priorId in priorIds)
                {
                    builtSessionIds.Add(priorId);
                }

                sessionIdsByKey[key] = priorIds;
                skippedKeys.Add(key);
                skippedCount += priorIds.Count;
                continue;
            }

            IReadOnlyList<CanonicalRecord> loaded = store.ListRecordsBySessionKey(key);
            List<CanonicalRecord> repriced = RepriceTurns(loaded);
            Dictionary<string, List<CanonicalRecord>> grouped = GroupByCanonicalHarness(repriced);
            if (grouped.Count == 0)
            {
                sessionIdsByKey[key] = [];
                skippedCount++;
                continue;
            }

            List<string> builtForKey = [];
            foreach (KeyValuePair<string, List<CanonicalRecord>> group in grouped)
            {
                string harness = group.Key;
                List<CanonicalRecord> groupRecords = group.Value;
                if (groupRecords.Count == 0 || !HasEvidence(groupRecords))
                {
                    skippedCount++;
                    continue;
                }

                IReadOnlyList<CanonicalRecord> merged = DedupeTwinTurns(groupRecords, key);
                if (merged.Count == 0 || !HasEvidence(merged))
                {
                    skippedCount++;
                    continue;
                }

                string sessionId = identities.Claim(key, harness);
                SessionSummaryRow parentRow = BuildSessionSummary(
                    sessionId,
                    merged,
                    counter.Count,
                    isSubagent: false,
                    parentSession: null);
                store.PutSession(parentRow);
                builtSessionIds.Add(sessionId);
                builtForKey.Add(sessionId);
                builtCount++;

                findingsCount += RebuildSessionFindings(sessionId, merged, identities);

                bool isCopilot = CanonPricing.IsCopilotHarness(harness) || string.Equals(harness, "copilot", StringComparison.Ordinal);
                if (isCopilot && !sessionId.Contains('#', StringComparison.Ordinal))
                {
                    foreach (CanonicalRecord invokeSpan in merged)
                    {
                        if (!IsInvokeAgentSpan(invokeSpan) || string.Equals(invokeSpan.SpanId, sessionId, StringComparison.Ordinal))
                        {
                            continue;
                        }

                        string subagentId = $"{sessionId}#{invokeSpan.SpanId}";
                        List<CanonicalRecord> descendants = CollectDescendantRecords(invokeSpan.SpanId, merged);
                        descendants.Sort(CompareRecordTimestamps);

                        string? earliestTimestamp = invokeSpan.Timestamp;
                        if (descendants.Count > 0)
                        {
                            double bestMs = RecordTimestampMs(invokeSpan);
                            foreach (CanonicalRecord desc in descendants)
                            {
                                double descMs = RecordTimestampMs(desc);
                                if (double.IsFinite(descMs) && (!double.IsFinite(bestMs) || descMs < bestMs))
                                {
                                    bestMs = descMs;
                                    earliestTimestamp = desc.Timestamp;
                                }
                            }
                        }

                        CanonicalRecord rootInvoke = invokeSpan with
                        {
                            ParentSpanId = null,
                            Timestamp = earliestTimestamp ?? invokeSpan.Timestamp,
                        };
                        List<CanonicalRecord> subagentRecords = [rootInvoke, .. descendants];
                        string? subAgentName = ExtractAgentName(invokeSpan);
                        string? taskSummary = ExtractTaskSummary(subagentRecords);
                        string subLabel = CanonPricing.FormatSessionTitle(new SessionTitleInput(
                            AgentName: subAgentName,
                            TaskSummary: taskSummary,
                            SessionId: subagentId,
                            Harness: harness));

                        SessionSummaryRow subRow = BuildSessionSummary(
                            subagentId,
                            subagentRecords,
                            counter.Count,
                            isSubagent: true,
                            parentSession: sessionId,
                            agentName: subAgentName,
                            label: subLabel,
                            includedInParent: true);
                        store.PutSession(subRow);
                        builtSessionIds.Add(subagentId);
                        builtForKey.Add(subagentId);
                        builtCount++;

                        findingsCount += RebuildSessionFindings(subagentId, subagentRecords, identities);
                    }
                }

                bool isClaude = IsClaudeHarness(harness) || merged.Any(static r => IsClaudeHarness(r.Harness));
                if (isClaude && !sessionId.StartsWith("agent-", StringComparison.Ordinal) && !sessionId.Contains('#', StringComparison.Ordinal))
                {
                    findingsCount += BuildClaudeSubagentSessions(
                        sessionId,
                        harness,
                        merged,
                        counter.Count,
                        identities,
                        builtSessionIds,
                        builtForKey,
                        ref builtCount);
                }
            }

            sessionIdsByKey[key] = builtForKey;
            rebuiltKeys.Add(key);
        }

        counter.Flush();

        int prunedCount = 0;
        foreach (string existingId in store.GetBuiltSessionIds())
        {
            if (builtSessionIds.Contains(existingId))
            {
                continue;
            }

            store.DeleteFindingsForSession(existingId);
            store.DeleteSession(existingId);
            prunedCount++;
        }

        long finalMarker = store.RecordsWriteMarker();
        store.SetMetadata(ProjectionRecordsMarkerKey, finalMarker.ToString(CultureInfo.InvariantCulture));
        store.SetMetadata(
            ProjectionSchemaVersionKey,
            store.GetMetadata("schema_version") ?? CanonSchema.SchemaVersion.ToString(CultureInfo.InvariantCulture));
        store.SetDetectorVersion(CanonSchema.DetectorVersion);
        store.RecordProjectedSessionKeys(finalMarker, sessionIdsByKey);

        return new ProjectionPassReport(
            Built: builtCount,
            Skipped: skippedCount,
            Pruned: prunedCount,
            Findings: findingsCount,
            RebuiltKeys: rebuiltKeys,
            SkippedKeys: skippedKeys);
    }

    private bool HasCurrentProjectionEnvironment()
    {
        if (legacyStore is not null)
        {
            string? recordedSchema = legacyStore.GetMetadata(ProjectionSchemaVersionKey);
            string? storeSchema = legacyStore.GetMetadata("schema_version");
            return legacyStore.HasCurrentDetectorVersion()
                && recordedSchema is not null
                && string.Equals(recordedSchema, storeSchema, StringComparison.Ordinal);
        }

        ProjectionStateView? projectionState = operationalStore!.GetProjectionState();
        return projectionState is not null && projectionState.ProjectionSchemaVersion == CanonSchema.SchemaVersion;
    }

    private int RebuildSessionFindings(
        string sessionId,
        IReadOnlyList<CanonicalRecord> sessionRecords,
        SessionIdentities identities)
    {
        ICanonStoreRepository store = legacyStore!;
        store.DeleteFindingsForSession(sessionId);
        OutcomeBlock outcome = CanonFindings.DeriveOutcome(sessionRecords);
        IReadOnlyList<Finding> findings = CanonFindings.DetectFindings(new DetectFindingsInput(
            RunId: sessionId,
            SessionId: sessionId,
            Records: sessionRecords,
            Outcome: outcome,
            SessionIdentityLookup: identities.IdFor));
        if (findings.Count > 0)
        {
            store.PutFindings(findings);
        }

        return findings.Count;
    }

    private int BuildClaudeSubagentSessions(
        string sessionId,
        string harness,
        IReadOnlyList<CanonicalRecord> merged,
        Func<string, long> countTokens,
        SessionIdentities identities,
        HashSet<string> builtSessionIds,
        List<string> builtForKey,
        ref int builtCount)
    {
        ICanonStoreRepository store = legacyStore!;
        Dictionary<string, string> recordAgentMap = new(StringComparer.Ordinal);
        foreach (CanonicalRecord record in merged)
        {
            string? agentId = ExtractClaudeAgentId(record);
            if (!string.IsNullOrEmpty(agentId))
            {
                recordAgentMap[record.SpanId] = agentId;
            }
        }

        foreach (CanonicalRecord record in merged)
        {
            if (!recordAgentMap.TryGetValue(record.SpanId, out string? agentId))
            {
                continue;
            }

            foreach (CanonicalRecord desc in CollectDescendantRecords(record.SpanId, merged))
            {
                recordAgentMap.TryAdd(desc.SpanId, agentId);
            }
        }

        Dictionary<string, List<CanonicalRecord>> groups = new(StringComparer.Ordinal);
        foreach (CanonicalRecord record in merged)
        {
            if (!recordAgentMap.TryGetValue(record.SpanId, out string? agentId))
            {
                continue;
            }

            string subId = ClaudeSubagentSessionId(sessionId, agentId);
            if (!groups.TryGetValue(subId, out List<CanonicalRecord>? list))
            {
                list = [];
                groups[subId] = list;
            }

            list.Add(record);
        }

        int findings = 0;
        foreach (KeyValuePair<string, List<CanonicalRecord>> kv in groups)
        {
            string subagentId = kv.Key;
            List<CanonicalRecord> groupRecords = kv.Value;
            if (groupRecords.Count == 0)
            {
                continue;
            }

            HashSet<string> groupSpanIds = new(groupRecords.Select(static r => r.SpanId), StringComparer.Ordinal);
            List<CanonicalRecord> prepared = groupRecords.Select(r =>
                r.ParentSpanId is null || !groupSpanIds.Contains(r.ParentSpanId)
                    ? r with { ParentSpanId = null }
                    : r).ToList();

            int rootIndex = prepared.FindIndex(static r => IsInvokeAgentSpan(r) || string.Equals(r.Op, "agent.invoke", StringComparison.Ordinal));
            if (rootIndex < 0 && prepared.Count > 0)
            {
                rootIndex = 0;
            }

            if (rootIndex >= 0)
            {
                string rootSpanId = prepared[rootIndex].SpanId;
                for (int i = 0; i < prepared.Count; i++)
                {
                    CanonicalRecord rec = prepared[i];
                    if (string.Equals(rec.SpanId, rootSpanId, StringComparison.Ordinal))
                    {
                        prepared[i] = rec with { ParentSpanId = null };
                    }
                    else if (rec.ParentSpanId is null || !groupSpanIds.Contains(rec.ParentSpanId))
                    {
                        prepared[i] = rec with { ParentSpanId = rootSpanId };
                    }
                }

                CanonicalRecord? earliest = prepared
                    .Where(static r => double.IsFinite(RecordTimestampMs(r)))
                    .OrderBy(RecordTimestampMs)
                    .FirstOrDefault();
                CanonicalRecord currentRoot = prepared[rootIndex];
                if (earliest is not null && !(RecordTimestampMs(currentRoot) <= RecordTimestampMs(earliest)))
                {
                    prepared[rootIndex] = currentRoot with { Timestamp = earliest.Timestamp };
                }
            }

            prepared.Sort(CompareRecordTimestamps);

            string? subAgentName = ExtractClaudeAgentName(prepared);
            string? taskSummary = ExtractClaudeDescription(prepared);
            string subLabel = CanonPricing.FormatSessionTitle(new SessionTitleInput(
                AgentName: subAgentName,
                TaskSummary: taskSummary,
                SessionId: subagentId,
                Harness: harness));

            string resolvedParent = sessionId;
            foreach (CanonicalRecord r in prepared)
            {
                string? p = AttributeOf(r, ["parent_session_id", "parent_session", "parentSessionId", "parentSession", "gen_ai.parent_session_id"]);
                if (!string.IsNullOrWhiteSpace(p))
                {
                    resolvedParent = p.Trim();
                    break;
                }
            }

            SessionSummaryRow subRow = BuildSessionSummary(
                subagentId,
                prepared,
                countTokens,
                isSubagent: true,
                parentSession: resolvedParent,
                agentName: subAgentName,
                label: subLabel,
                includedInParent: string.Equals(resolvedParent, sessionId, StringComparison.Ordinal));
            store.PutSession(subRow);
            builtSessionIds.Add(subagentId);
            builtForKey.Add(subagentId);
            builtCount++;

            findings += RebuildSessionFindings(subagentId, prepared, identities);
        }

        return findings;
    }

    private List<CanonicalRecord> RepriceTurns(IReadOnlyList<CanonicalRecord> records)
    {
        ICanonStoreRepository store = legacyStore!;
        Dictionary<string, CostBlock> changes = new(StringComparer.Ordinal);
        List<CanonicalRecord> updated = new(records.Count);
        foreach (CanonicalRecord record in records)
        {
            if (!string.Equals(record.Op, "llm.invoke", StringComparison.Ordinal) && !IsRollup(record))
            {
                updated.Add(record);
                continue;
            }

            string? model = AttributeOf(record, ModelKeys);
            CostBlock next;
            if (CanonPricing.IsPublishedTableHarness(record.Harness))
            {
                next = CanonPricing.PricePublishedTurn(record.Tokens, model, record.Harness, record.Cost);
            }
            else if (CanonPricing.IsCopilotHarness(record.Harness))
            {
                next = CanonPricing.PriceCopilotTurn(record.Tokens, model, record.Harness, record.Cost);
            }
            else
            {
                updated.Add(record);
                continue;
            }

            if (string.Equals(
                    JsonSerializer.Serialize(next, ProjectionJsonOptions),
                    JsonSerializer.Serialize(record.Cost, ProjectionJsonOptions),
                    StringComparison.Ordinal))
            {
                updated.Add(record);
                continue;
            }

            changes[record.SpanId] = next;
            updated.Add(record with { Cost = next });
        }

        if (changes.Count > 0)
        {
            store.SetCosts(changes);
        }

        return updated;
    }

    private static Dictionary<string, object?> SerializeContextPayload(
        ContextAnalysis context,
        string? contextLimitSource,
        IReadOnlyList<CanonicalRecord> measuredTurns,
        long? counterInput,
        int unmeasuredTurns,
        IReadOnlyDictionary<string, JsonElement>? totalMeasurability)
    {
        Dictionary<string, string> bucketMap = new(StringComparer.Ordinal)
        {
            ["system_prompt"] = "system_prompt",
            ["conversation_history"] = "conversation_history",
            ["tool_definitions"] = "tool_definitions",
            ["tool_results"] = "tool_result_content",
            ["response"] = "response",
        };

        bool hideLimit = string.Equals(contextLimitSource, "absent", StringComparison.Ordinal);
        List<Dictionary<string, object?>> serializedTurns = [];
        if (context.Measurable)
        {
            foreach (TurnPressure turn in context.Turns)
            {
                Dictionary<string, object?> entry = new(StringComparer.Ordinal)
                {
                    ["index"] = turn.Index,
                    ["inputTokens"] = turn.InputTokens,
                    ["reported_input"] = turn.InputTokens,
                    ["buckets"] = turn.Buckets,
                    ["toolDefinitionsByServer"] = turn.ToolDefinitionsByServer,
                    ["builtinToolDefinitionTokens"] = turn.BuiltinToolDefinitionTokens,
                    ["strippedInstructionBlocks"] = turn.StrippedInstructionBlocks,
                    ["bucketedTokens"] = turn.BucketedTokens,
                    ["residual"] = turn.Residual,
                    ["accumulationRate"] = turn.AccumulationRate,
                    ["freshInput"] = turn.FreshInput,
                };

                if (!hideLimit)
                {
                    entry["headroom"] = turn.Headroom;
                    entry["pressure"] = turn.Pressure;
                }

                if (turn.FreshInputJump is not null)
                {
                    entry["freshInputJump"] = new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["previous"] = turn.FreshInputJump.Previous,
                        ["factor"] = double.IsFinite(turn.FreshInputJump.Factor) ? turn.FreshInputJump.Factor : null,
                    };
                }

                if (turn.NonResidentToolDefinitions.HasValue)
                {
                    entry["nonResidentToolDefinitions"] = turn.NonResidentToolDefinitions.Value;
                }

                serializedTurns.Add(entry);
            }
        }

        Dictionary<string, object?> firstBucket = BuildContextBucket(
            context.Measurable && context.Turns.Count > 0 ? context.Turns[0] : null,
            measuredTurns.Count > 0 ? measuredTurns[0].Tokens.ReportedInput : (counterInput ?? 0),
            bucketMap,
            totalMeasurability);
        Dictionary<string, object?> lastBucket = BuildContextBucket(
            context.Measurable && context.Turns.Count > 0 ? context.Turns[^1] : null,
            measuredTurns.Count > 0 ? measuredTurns[^1].Tokens.ReportedInput : (counterInput ?? 0),
            bucketMap,
            totalMeasurability);

        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["measurable"] = context.Measurable,
            ["reason"] = context.Reason,
            ["contextLimit"] = hideLimit ? 0L : context.ContextLimit,
            ["contextLimitSource"] = contextLimitSource,
            ["turnCount"] = context.TurnCount,
            ["turns"] = serializedTurns,
            ["observedParts"] = context.ObservedParts,
            ["residualTotal"] = context.ResidualTotal,
            ["derivedCounts"] = context.DerivedCounts,
            ["freshJumpFactor"] = context.FreshJumpFactor,
            ["flaggedTurns"] = context.FlaggedTurns,
            ["sessionAccumulationRate"] = context.SessionAccumulationRate,
            ["first"] = firstBucket,
            ["last"] = lastBucket,
            ["unmeasuredTurns"] = unmeasuredTurns,
        };
    }

    private static Dictionary<string, object?> BuildContextBucket(
        TurnPressure? turn,
        long reportedInput,
        Dictionary<string, string> bucketMap,
        IReadOnlyDictionary<string, JsonElement>? totalMeasurability)
    {
        Dictionary<string, object?> buckets = new(StringComparer.Ordinal);
        if (turn is not null)
        {
            foreach (KeyValuePair<string, long> kv in turn.Buckets)
            {
                buckets[kv.Key] = kv.Value;
            }
        }
        else if (totalMeasurability is not null)
        {
            foreach (KeyValuePair<string, string> kv in bucketMap)
            {
                if (TryUnavailable(totalMeasurability, kv.Value, out JsonElement unavail))
                {
                    buckets[kv.Value] = unavail;
                    buckets[kv.Key] = unavail;
                }
            }
        }

        object reported = TryUnavailable(totalMeasurability, "token_usage", out JsonElement tokUnavail)
            ? tokUnavail
            : reportedInput;

        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["buckets"] = buckets,
            ["reported_input"] = reported,
        };
    }

    private static Dictionary<string, object?> WithoutAttributes(TimelineNode node) =>
        new(StringComparer.Ordinal)
        {
            ["spanId"] = node.SpanId,
            ["parentId"] = node.ParentId,
            ["startMs"] = node.StartMs,
            ["durationMs"] = node.DurationMs,
            ["kind"] = node.Kind,
            ["name"] = node.Name,
            ["status"] = node.Status,
            ["attributes"] = new Dictionary<string, object?>(StringComparer.Ordinal),
            ["isSubagent"] = node.IsSubagent,
            ["isAuxiliary"] = node.IsAuxiliary,
            ["cost"] = node.Cost,
            ["children"] = node.Children.Select(WithoutAttributes).ToList(),
        };

    private static HashSet<CanonicalRecord> CollapseIdJoins(
        IReadOnlyList<CanonicalRecord> records,
        string? shareKey,
        HashSet<CanonicalRecord> dropped,
        Dictionary<CanonicalRecord, CanonicalRecord> transplant)
    {
        HashSet<CanonicalRecord> joined = new(ReferenceEqualityComparer.Instance);
        Dictionary<string, CanonicalRecord> keeperByMessageId = new(StringComparer.Ordinal);
        Dictionary<string, CanonicalRecord> keeperByRequestId = new(StringComparer.Ordinal);

        void RememberEarliest(Dictionary<string, CanonicalRecord> map, string key, CanonicalRecord record)
        {
            if (!map.TryGetValue(key, out CanonicalRecord? known)
                || RecordTimestampMs(record) < RecordTimestampMs(known))
            {
                map[key] = record;
            }
        }

        foreach (CanonicalRecord record in records)
        {
            if (!string.Equals(record.Op, "llm.invoke", StringComparison.Ordinal) || IsFileSource(record.Source))
            {
                continue;
            }

            string session = SessionKeyOf(record, shareKey);
            string? messageId = OtelResponseIdOf(record);
            if (messageId is not null)
            {
                RememberEarliest(keeperByMessageId, $"{session}\u0000{messageId}", record);
            }

            string? requestId = OtelRequestJoinId(record);
            if (requestId is not null)
            {
                RememberEarliest(keeperByRequestId, $"{session}\u0000{requestId}", record);
            }
        }

        if (keeperByMessageId.Count == 0 && keeperByRequestId.Count == 0)
        {
            return joined;
        }

        void Adopt(CanonicalRecord keeper, CanonicalRecord file)
        {
            dropped.Add(file);
            joined.Add(file);
            joined.Add(keeper);
            if (!HasParts(file))
            {
                return;
            }

            transplant[keeper] = transplant.TryGetValue(keeper, out CanonicalRecord? existing)
                ? MergeDonors(existing, file)
                : file;
        }

        foreach (CanonicalRecord record in records)
        {
            if (!string.Equals(record.Op, "llm.invoke", StringComparison.Ordinal) || !IsFileSource(record.Source))
            {
                continue;
            }

            string session = SessionKeyOf(record, shareKey);
            string? messageId = FileNativeIdOf(record);
            if (messageId is not null && keeperByMessageId.TryGetValue($"{session}\u0000{messageId}", out CanonicalRecord? byMessage))
            {
                Adopt(byMessage, record);
                continue;
            }

            string? requestId = FileRequestIdOf(record);
            if (requestId is not null && keeperByRequestId.TryGetValue($"{session}\u0000{requestId}", out CanonicalRecord? byRequest))
            {
                Adopt(byRequest, record);
            }
        }

        return joined;
    }

    private static void CollapseCluster(
        IReadOnlyList<CanonicalRecord> cluster,
        HashSet<CanonicalRecord> dropped,
        Dictionary<CanonicalRecord, CanonicalRecord> transplant)
    {
        if (cluster.Count == 0)
        {
            return;
        }

        List<CanonicalRecord> otels = cluster.Where(static r => !IsFileSource(r.Source)).ToList();
        List<CanonicalRecord> files = cluster.Where(static r => IsFileSource(r.Source)).ToList();
        if (otels.Count == 0)
        {
            if (files.Count <= 1)
            {
                return;
            }

            if (!files.All(static f => string.Equals(f.Source, "codeburn/claude-desktop", StringComparison.Ordinal)
                    || string.Equals(f.Harness, "claude-desktop", StringComparison.Ordinal)))
            {
                return;
            }

            files.Sort(CompareRecordTimestamps);
            CanonicalRecord keeper = files[0];
            CanonicalRecord? donor = null;
            for (int i = 1; i < files.Count; i++)
            {
                CanonicalRecord file = files[i];
                dropped.Add(file);
                if (HasParts(file))
                {
                    donor = donor is null ? file : MergeDonors(donor, file);
                }
            }

            if (donor is not null)
            {
                transplant[keeper] = donor;
            }

            return;
        }

        if (files.Count == 0)
        {
            return;
        }

        foreach (CanonicalRecord file in files)
        {
            dropped.Add(file);
        }

        List<CanonicalRecord> donors = files.Where(HasParts).OrderBy(RecordTimestampMs).ToList();
        HashSet<CanonicalRecord> free = new(donors, ReferenceEqualityComparer.Instance);

        List<CanonicalRecord> keepers =
        [
            .. otels.Where(static o => !HasParts(o)).OrderBy(RecordTimestampMs),
            .. otels.Where(HasParts).OrderBy(RecordTimestampMs),
        ];

        foreach (CanonicalRecord keeper in keepers)
        {
            double moment = RecordTimestampMs(keeper);
            CanonicalRecord? best = null;
            double bestDist = double.PositiveInfinity;
            foreach (CanonicalRecord donor in donors)
            {
                if (!free.Contains(donor))
                {
                    continue;
                }

                double dist = Math.Abs(RecordTimestampMs(donor) - moment);
                if (best is null || dist < bestDist)
                {
                    best = donor;
                    bestDist = dist;
                }
            }

            if (best is null)
            {
                break;
            }

            free.Remove(best);
            transplant[keeper] = best;
        }

        foreach (CanonicalRecord donor in donors.Where(free.Contains))
        {
            double donorMoment = RecordTimestampMs(donor);
            CanonicalRecord nearest = otels[0];
            double bestDist = Math.Abs(RecordTimestampMs(nearest) - donorMoment);
            foreach (CanonicalRecord candidate in otels.Skip(1))
            {
                double dist = Math.Abs(RecordTimestampMs(candidate) - donorMoment);
                if (dist < bestDist)
                {
                    nearest = candidate;
                    bestDist = dist;
                }
            }

            transplant[nearest] = transplant.TryGetValue(nearest, out CanonicalRecord? existing)
                ? MergeDonors(existing, donor)
                : donor;
        }
    }

    private static void CollapseOverlapFileJoins(
        IReadOnlyList<CanonicalRecord> records,
        string? shareKey,
        HashSet<CanonicalRecord> dropped,
        Dictionary<CanonicalRecord, CanonicalRecord> transplant,
        Dictionary<CanonicalRecord, TokenUsage> tokenOverride)
    {
        Dictionary<string, List<CanonicalRecord>> bySession = new(StringComparer.Ordinal);
        foreach (CanonicalRecord record in records)
        {
            if (dropped.Contains(record))
            {
                continue;
            }

            if (!string.Equals(record.Op, "llm.invoke", StringComparison.Ordinal))
            {
                continue;
            }

            if (!IsFileSource(record.Source) || IsUnreportedCounters(record.Tokens))
            {
                continue;
            }

            string key = SessionKeyOf(record, shareKey);
            if (!bySession.TryGetValue(key, out List<CanonicalRecord>? list))
            {
                list = [];
                bySession[key] = list;
            }

            list.Add(record);
        }

        foreach (List<CanonicalRecord> turns in bySession.Values)
        {
            if (!HasEvidencedCursorTwinShare(turns))
            {
                continue;
            }

            turns.Sort(CompareRecordTimestamps);
            List<CanonicalRecord> cluster = [];
            foreach (CanonicalRecord record in turns)
            {
                if (cluster.Count > 0 && RecordTimestampMs(record) - RecordTimestampMs(cluster[0]) > TwinTurnMaxSkewMs)
                {
                    CollapseOverlapCluster(cluster, dropped, transplant, tokenOverride);
                    cluster.Clear();
                }

                cluster.Add(record);
            }

            CollapseOverlapCluster(cluster, dropped, transplant, tokenOverride);
        }
    }

    private static void CollapseOverlapCluster(
        IReadOnlyList<CanonicalRecord> cluster,
        HashSet<CanonicalRecord> dropped,
        Dictionary<CanonicalRecord, CanonicalRecord> transplant,
        Dictionary<CanonicalRecord, TokenUsage> tokenOverride)
    {
        if (cluster.Count < 2)
        {
            return;
        }

        List<CanonicalRecord> completes = cluster.Where(IsCompleteCursorAgentObservation).ToList();
        if (completes.Count == 0)
        {
            return;
        }

        Dictionary<CanonicalRecord, List<CanonicalRecord>> groups = new(ReferenceEqualityComparer.Instance);
        foreach (CanonicalRecord complete in completes)
        {
            groups[complete] = [complete];
        }

        List<CanonicalRecord> partials = cluster
            .Where(static r => !IsCompleteCursorAgentObservation(r) && !IsCompleteCursorIdeObservation(r))
            .OrderBy(RecordTimestampMs)
            .ToList();

        foreach (CanonicalRecord partial in partials)
        {
            CanonicalRecord? best = null;
            double bestDist = double.PositiveInfinity;
            double partialTime = RecordTimestampMs(partial);
            foreach (CanonicalRecord complete in completes)
            {
                if (!CanJoinDistinctCounters(partial.Tokens, complete.Tokens))
                {
                    continue;
                }

                double completeTime = RecordTimestampMs(complete);
                double dist = Math.Abs(partialTime - completeTime);
                if (best is null || dist < bestDist || (dist == bestDist && completeTime < RecordTimestampMs(best)))
                {
                    best = complete;
                    bestDist = dist;
                }
            }

            if (best is not null)
            {
                groups[best].Add(partial);
            }
        }

        HashSet<CanonicalRecord> availableIdes = new(cluster.Where(IsCompleteCursorIdeObservation), ReferenceEqualityComparer.Instance);
        foreach (CanonicalRecord complete in completes.OrderBy(RecordTimestampMs))
        {
            CanonicalRecord? best = null;
            double bestDist = double.PositiveInfinity;
            double completeTime = RecordTimestampMs(complete);
            foreach (CanonicalRecord ide in availableIdes)
            {
                if (!CanJoinDistinctCounters(ide.Tokens, complete.Tokens))
                {
                    continue;
                }

                double ideTime = RecordTimestampMs(ide);
                double dist = Math.Abs(ideTime - completeTime);
                if (best is null || dist < bestDist || (dist == bestDist && ideTime < RecordTimestampMs(best)))
                {
                    best = ide;
                    bestDist = dist;
                }
            }

            if (best is not null)
            {
                availableIdes.Remove(best);
                groups[complete].Add(best);
            }
        }

        foreach (List<CanonicalRecord> members in groups.Values)
        {
            if (members.Count < 2)
            {
                continue;
            }

            CanonicalRecord keeper = members[0];
            for (int i = 1; i < members.Count; i++)
            {
                keeper = PreferFullerKeeper(keeper, members[i]);
            }

            TokenUsage mergedTokens = keeper.Tokens;
            foreach (CanonicalRecord member in members)
            {
                if (ReferenceEquals(member, keeper))
                {
                    continue;
                }

                mergedTokens = MaxTokens(mergedTokens, member.Tokens);
                dropped.Add(member);
                if (!HasParts(member))
                {
                    continue;
                }

                transplant[keeper] = transplant.TryGetValue(keeper, out CanonicalRecord? existing)
                    ? MergeDonors(existing, member)
                    : member;
            }

            if (mergedTokens != keeper.Tokens)
            {
                tokenOverride[keeper] = mergedTokens;
            }
        }
    }

    private static CanonicalRecord MergeOtelFirst(CanonicalRecord record, CanonicalRecord donor)
    {
        IReadOnlyList<ContentPart> keeperParts = record.Parts ?? [];
        HashSet<string> keeperBuckets = new(keeperParts.Select(static p => p.Part), StringComparer.Ordinal);
        HashSet<string> held = new([.. keeperBuckets, .. record.Content.Keys], StringComparer.Ordinal);

        List<ContentPart> fillParts = (donor.Parts ?? []).Where(p => !held.Contains(p.Part)).ToList();
        Dictionary<string, string> fillContent = new(StringComparer.Ordinal);
        foreach (KeyValuePair<string, string> kv in donor.Content)
        {
            if (!held.Contains(kv.Key))
            {
                fillContent[kv.Key] = kv.Value;
            }
        }

        if (keeperParts.Count == 0 && fillParts.Count == 0)
        {
            return record with { Content = MergeDictionaries(fillContent, record.Content) };
        }

        HashSet<string> fillBuckets = new(fillParts.Select(static p => p.Part), StringComparer.Ordinal);
        List<ContentPart> otelFlatParts = record.Content
            .Where(kv => !keeperBuckets.Contains(kv.Key))
            .Select(static kv => new ContentPart(kv.Key, kv.Value))
            .ToList();
        List<ContentPart> fileFlatParts = fillContent
            .Where(kv => !fillBuckets.Contains(kv.Key))
            .Select(static kv => new ContentPart(kv.Key, kv.Value))
            .ToList();

        List<ContentPart> parts = [.. keeperParts, .. otelFlatParts, .. fillParts, .. fileFlatParts];
        Dictionary<string, string> mergedContent = MergeDictionaries(fillContent, record.Content);
        mergedContent = MergeDictionaries(mergedContent, CanonValidation.ContentFromParts(parts));
        return record with { Parts = parts, Content = mergedContent };
    }

    private static CanonicalRecord MergeDonors(CanonicalRecord first, CanonicalRecord second)
    {
        List<ContentPart> parts = [.. first.Parts ?? [], .. second.Parts ?? []];
        Dictionary<string, string> merged = MergeDictionaries(first.Content, second.Content);
        if (parts.Count > 0)
        {
            merged = MergeDictionaries(merged, CanonValidation.ContentFromParts(parts));
        }

        return first with { Parts = parts, Content = merged };
    }

    private static bool IsFileSource(string source) =>
        source.StartsWith(FileSourcePrefix, StringComparison.Ordinal);

    private static string SessionKeyOf(CanonicalRecord record, string? shareKey) =>
        record.SessionId ?? shareKey ?? string.Empty;

    private static string CounterKey(CanonicalRecord record, string? shareKey)
    {
        TokenUsage t = record.Tokens;
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{SessionKeyOf(record, shareKey)}:{t.FreshInput}:{t.CacheRead}:{t.CacheCreation}:{t.Output}:{t.ReportedInput}:{t.ReportedOutput}");
    }

    private static bool IsUnreportedCounters(TokenUsage tokens) =>
        tokens.FreshInput == 0
        && tokens.CacheRead == 0
        && tokens.CacheCreation == 0
        && tokens.Output == 0
        && tokens.ReportedInput == 0
        && tokens.ReportedOutput == 0;

    private static long[] TokenDims(TokenUsage tokens) =>
    [
        tokens.FreshInput,
        tokens.CacheRead,
        tokens.CacheCreation,
        tokens.Output,
        tokens.ReportedInput,
        tokens.ReportedOutput,
    ];

    private static bool CanJoinDistinctCounters(TokenUsage a, TokenUsage b)
    {
        long[] da = TokenDims(a);
        long[] db = TokenDims(b);
        bool equal = true;
        bool aSubB = true;
        bool bSubA = true;
        bool comp = true;

        for (int i = 0; i < da.Length; i++)
        {
            if (da[i] != db[i])
            {
                equal = false;
            }

            if (da[i] > db[i])
            {
                aSubB = false;
            }

            if (db[i] > da[i])
            {
                bSubA = false;
            }

            if (da[i] != 0 && db[i] != 0 && da[i] != db[i])
            {
                comp = false;
            }
        }

        return !equal && (comp || aSubB || bSubA);
    }

    private static TokenUsage MaxTokens(TokenUsage a, TokenUsage b)
    {
        long? reasoning = a.Reasoning.HasValue || b.Reasoning.HasValue
            ? Math.Max(a.Reasoning ?? 0, b.Reasoning ?? 0)
            : null;
        return new TokenUsage(
            FreshInput: Math.Max(a.FreshInput, b.FreshInput),
            CacheRead: Math.Max(a.CacheRead, b.CacheRead),
            CacheCreation: Math.Max(a.CacheCreation, b.CacheCreation),
            Output: Math.Max(a.Output, b.Output),
            ReportedInput: Math.Max(a.ReportedInput, b.ReportedInput),
            ReportedOutput: Math.Max(a.ReportedOutput, b.ReportedOutput),
            Reasoning: reasoning);
    }

    private static CanonicalRecord PreferFullerKeeper(CanonicalRecord a, CanonicalRecord b)
    {
        (int nonZeroA, long magA) = CounterFullness(a.Tokens);
        (int nonZeroB, long magB) = CounterFullness(b.Tokens);
        if (nonZeroA != nonZeroB)
        {
            return nonZeroA > nonZeroB ? a : b;
        }

        if (magA != magB)
        {
            return magA > magB ? a : b;
        }

        return RecordTimestampMs(a) <= RecordTimestampMs(b) ? a : b;
    }

    private static (int NonZero, long Magnitude) CounterFullness(TokenUsage tokens)
    {
        int nonZero = 0;
        long mag = 0;
        foreach (long v in TokenDims(tokens))
        {
            if (v > 0)
            {
                nonZero++;
            }

            mag += v;
        }

        return (nonZero, mag);
    }

    private static bool IsCursorCollector(CanonicalRecord record) =>
        !string.Equals(record.Harness, "cursor-agent", StringComparison.Ordinal)
        && (string.Equals(record.Harness, "cursor", StringComparison.Ordinal)
            || string.Equals(record.Source, "codeburn/cursor", StringComparison.Ordinal));

    private static bool IsCursorAgentCollector(CanonicalRecord record) =>
        string.Equals(record.Harness, "cursor-agent", StringComparison.Ordinal)
        || record.Source.Contains("cursor-agent", StringComparison.Ordinal);

    private static bool HasEvidencedCursorTwinShare(IReadOnlyList<CanonicalRecord> records)
    {
        bool cursor = false;
        bool agent = false;
        foreach (CanonicalRecord record in records)
        {
            if (IsCursorAgentCollector(record))
            {
                agent = true;
            }
            else if (IsCursorCollector(record))
            {
                cursor = true;
            }

            if (cursor && agent)
            {
                return true;
            }
        }

        return false;
    }

    private static bool HasCompleteCounters(CanonicalRecord record) =>
        (record.Tokens.FreshInput > 0 || record.Tokens.ReportedInput > 0)
        && (record.Tokens.Output > 0 || record.Tokens.ReportedOutput > 0);

    private static bool IsCompleteCursorAgentObservation(CanonicalRecord record) =>
        IsCursorAgentCollector(record) && HasCompleteCounters(record);

    private static bool IsCompleteCursorIdeObservation(CanonicalRecord record) =>
        IsCursorCollector(record) && HasCompleteCounters(record);

    private static string? OtelResponseIdOf(CanonicalRecord record) => RawString(record, "gen_ai.response.id");

    private static string? OtelRequestJoinId(CanonicalRecord record)
    {
        string? requestId = RawString(record, "request_id");
        if (requestId is not null)
        {
            return requestId;
        }

        string? responseId = OtelResponseIdOf(record);
        return responseId is not null && responseId.StartsWith("req_", StringComparison.Ordinal) ? responseId : null;
    }

    private static string? FileRequestIdOf(CanonicalRecord record) => RawString(record, "requestId");

    private static string? FileNativeIdOf(CanonicalRecord record)
    {
        if (record.Raw is null
            || !record.Raw.TryGetValue("provenance", out JsonElement provEl)
            || provEl.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (provEl.TryGetProperty("recordDigest", out JsonElement digestEl) && digestEl.ValueKind != JsonValueKind.Undefined && digestEl.ValueKind != JsonValueKind.Null)
        {
            return null;
        }

        if (provEl.TryGetProperty("nativeRecordId", out JsonElement idEl) && idEl.ValueKind == JsonValueKind.String)
        {
            string? val = idEl.GetString();
            return string.IsNullOrEmpty(val) ? null : val;
        }

        return null;
    }

    private static string? RawString(CanonicalRecord record, string key)
    {
        if (record.Raw is null || !record.Raw.TryGetValue(key, out JsonElement el) || el.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        string? val = el.GetString();
        return string.IsNullOrEmpty(val) ? null : val;
    }

    private static bool HasParts(CanonicalRecord record) =>
        (record.Parts is { Count: > 0 }) || record.Content.Count > 0;

    private static Dictionary<string, string> MergeDictionaries(
        IReadOnlyDictionary<string, string> first,
        IReadOnlyDictionary<string, string> second)
    {
        Dictionary<string, string> result = new(first.Count + second.Count, StringComparer.Ordinal);
        foreach (KeyValuePair<string, string> kv in first)
        {
            result[kv.Key] = kv.Value;
        }

        foreach (KeyValuePair<string, string> kv in second)
        {
            result[kv.Key] = kv.Value;
        }

        return result;
    }

    private static double RecordTimestampMs(CanonicalRecord record) =>
        DateTimeOffset.TryParse(record.Timestamp, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTimeOffset dto)
            ? dto.ToUnixTimeMilliseconds()
            : double.NaN;

    private static int CompareRecordTimestamps(CanonicalRecord a, CanonicalRecord b)
    {
        double ta = RecordTimestampMs(a);
        double tb = RecordTimestampMs(b);
        bool aOk = double.IsFinite(ta);
        bool bOk = double.IsFinite(tb);
        if (aOk && bOk)
        {
            return ta.CompareTo(tb);
        }

        if (aOk)
        {
            return -1;
        }

        return bOk ? 1 : 0;
    }

    private static DroidSessionSplit SplitDroidSessionRecords(IReadOnlyList<CanonicalRecord> records)
    {
        List<CanonicalRecord> turnRecords = [];
        List<CanonicalRecord> counterRecords = [];
        List<CanonicalRecord> fileToolRecords = [];

        foreach (CanonicalRecord record in records)
        {
            if (!string.Equals(record.Harness, DroidHarness, StringComparison.Ordinal))
            {
                continue;
            }

            if (string.Equals(record.Source, DroidPluginSource, StringComparison.Ordinal))
            {
                turnRecords.Add(record);
                continue;
            }

            if (IsFileSource(record.Source))
            {
                counterRecords.Add(record);
                if (string.Equals(record.Op, "tool.invoke", StringComparison.Ordinal))
                {
                    fileToolRecords.Add(record);
                }

                continue;
            }

            turnRecords.Add(record);
        }

        return new DroidSessionSplit(turnRecords, counterRecords, fileToolRecords);
    }

    private static bool IsRollup(CanonicalRecord record) =>
        string.Equals(record.Op, "agent.invoke", StringComparison.Ordinal)
        || (string.Equals(record.Op, "llm.invoke", StringComparison.Ordinal)
            && string.Equals(AttributeOf(record, ["gen_ai.operation.name"]), "invoke_agent", StringComparison.Ordinal));

    private static List<CanonicalRecord> TurnsOf(IReadOnlyList<CanonicalRecord> records)
    {
        List<CanonicalRecord> calls = records
            .Where(static r => string.Equals(r.Op, "llm.invoke", StringComparison.Ordinal) && !IsRollup(r))
            .ToList();
        if (calls.Count > 0)
        {
            return calls;
        }

        return records
            .Where(static r => IsRollup(r) && (CanonPricing.MeasuredInput(r.Tokens) > 0 || r.Tokens.Output > 0))
            .ToList();
    }

    private static bool IsSideAgentTurn(CanonicalRecord record)
    {
        string? agentName = AttributeOf(record, ["gen_ai.agent.name", "agent.name", "agentName"]);
        return agentName is not null && SideAgentNames.Contains(agentName);
    }

    private static List<ContextPart> PartsOf(CanonicalRecord record)
    {
        if (record.Parts is { Count: > 0 })
        {
            return record.Parts.Select(ContextPart.FromContentPart).ToList();
        }

        return record.Content.Select(static kv => new ContextPart(kv.Key, kv.Value)).ToList();
    }

    private static HashSet<string> CarriedBucketsOf(IReadOnlyList<CanonicalRecord> records)
    {
        HashSet<string> carried = new(StringComparer.Ordinal);
        foreach (CanonicalRecord record in records)
        {
            foreach (ContextPart part in PartsOf(record))
            {
                if (part.Text.Length > 0)
                {
                    carried.Add(part.Part);
                }
            }
        }

        return carried;
    }

    private static Dictionary<string, JsonElement>? MergeMeasurability(IReadOnlyList<CanonicalRecord> records)
    {
        Dictionary<string, List<JsonElement>> seen = new(StringComparer.Ordinal);
        foreach (CanonicalRecord record in records)
        {
            if (record.Measurability is null)
            {
                continue;
            }

            foreach (KeyValuePair<string, JsonElement> kv in record.Measurability)
            {
                if (!seen.TryGetValue(kv.Key, out List<JsonElement>? list))
                {
                    list = [];
                    seen[kv.Key] = list;
                }

                list.Add(kv.Value);
            }
        }

        if (seen.Count == 0)
        {
            return null;
        }

        Dictionary<string, JsonElement> merged = new(StringComparer.Ordinal);
        foreach (KeyValuePair<string, List<JsonElement>> kv in seen)
        {
            JsonElement? measured = null;
            JsonElement? derived = null;
            JsonElement? notMeasurable = null;
            foreach (JsonElement el in kv.Value)
            {
                if (el.ValueKind == JsonValueKind.String)
                {
                    string? s = el.GetString();
                    if (string.Equals(s, "measured", StringComparison.Ordinal))
                    {
                        measured ??= el.Clone();
                    }
                    else if (string.Equals(s, "derived", StringComparison.Ordinal))
                    {
                        derived ??= el.Clone();
                    }
                }
                else if (el.ValueKind == JsonValueKind.Object
                         && el.TryGetProperty("availability", out JsonElement avail)
                         && string.Equals(avail.GetString(), "not_measurable", StringComparison.Ordinal))
                {
                    notMeasurable ??= el.Clone();
                }
            }

            merged[kv.Key] = measured ?? derived ?? notMeasurable
                ?? JsonSerializer.Deserialize<JsonElement>("{\"availability\":\"not_measurable\",\"reason\":\"The source did not provide this metric.\"}");
        }

        return merged;
    }

    private static IReadOnlyDictionary<string, JsonElement>? CompositionDeclaration(
        Dictionary<string, JsonElement>? measurability,
        HashSet<string> carriedBuckets)
    {
        if (measurability is null)
        {
            return null;
        }

        Dictionary<string, JsonElement> adjusted = new(measurability, StringComparer.Ordinal);
        foreach (string key in carriedBuckets)
        {
            if (adjusted.TryGetValue(key, out JsonElement el)
                && el.ValueKind == JsonValueKind.Object
                && el.TryGetProperty("availability", out JsonElement avail)
                && string.Equals(avail.GetString(), "not_measurable", StringComparison.Ordinal))
            {
                adjusted.Remove(key);
            }
        }

        return adjusted;
    }

    private static List<ToolDefinition> ToolDefinitionsOf(IReadOnlyList<CanonicalRecord> records, Func<string, long> countTokens)
    {
        Dictionary<string, (string? Server, long Tokens, int TurnsResident)> byName = new(StringComparer.Ordinal);
        foreach (CanonicalRecord record in records)
        {
            List<(string Text, string? Server, bool Catalogue)> rawDefs = [];
            if (record.Parts is { Count: > 0 })
            {
                foreach (ContentPart p in record.Parts)
                {
                    bool isDefs = string.Equals(p.Part, "tool_definitions", StringComparison.Ordinal);
                    bool isDef = string.Equals(p.Part, "tool_definition", StringComparison.Ordinal);
                    if (!isDefs && !isDef)
                    {
                        continue;
                    }

                    rawDefs.Add((p.Text, p.Server, isDefs));
                }
            }
            else
            {
                if (record.Content.TryGetValue("tool_definitions", out string? td))
                {
                    rawDefs.Add((td, null, false));
                }

                if (record.Content.TryGetValue("tool_definition", out string? t))
                {
                    rawDefs.Add((t, null, false));
                }
            }

            foreach ((string text, string? server, bool catalogue) in rawDefs)
            {
                if (!catalogue || string.IsNullOrWhiteSpace(text))
                {
                    continue;
                }

                foreach ((string name, string itemText) in SplitToolDefinitionsText(text))
                {
                    if (string.IsNullOrWhiteSpace(name))
                    {
                        continue;
                    }

                    string trimmed = name.Trim();
                    if (!byName.TryGetValue(trimmed, out (string? Server, long Tokens, int TurnsResident) existing))
                    {
                        byName[trimmed] = (server, countTokens(itemText), 1);
                    }
                    else
                    {
                        byName[trimmed] = existing with { TurnsResident = existing.TurnsResident + 1 };
                    }
                }
            }
        }

        return byName.Select(kv => new ToolDefinition(kv.Key, kv.Value.Server, kv.Value.Tokens, kv.Value.TurnsResident)).ToList();
    }

    private static List<(string Name, string Text)> SplitToolDefinitionsText(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        try
        {
            using JsonDocument doc = JsonDocument.Parse(text);
            JsonElement root = doc.RootElement;
            IEnumerable<JsonElement> items = root.ValueKind == JsonValueKind.Array
                ? root.EnumerateArray()
                : root.ValueKind == JsonValueKind.Object && root.TryGetProperty("tools", out JsonElement toolsEl) && toolsEl.ValueKind == JsonValueKind.Array
                    ? toolsEl.EnumerateArray()
                    : [root];

            List<(string Name, string Text)> result = [];
            foreach (JsonElement item in items)
            {
                if (item.ValueKind == JsonValueKind.String)
                {
                    string? s = item.GetString();
                    if (!string.IsNullOrWhiteSpace(s))
                    {
                        result.Add((s.Trim(), s));
                    }
                }
                else if (item.ValueKind == JsonValueKind.Object)
                {
                    string itemText = item.GetRawText();
                    string name = string.Empty;
                    if (item.TryGetProperty("name", out JsonElement n) && n.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(n.GetString()))
                    {
                        name = n.GetString()!.Trim();
                    }
                    else if (item.TryGetProperty("function", out JsonElement fn) && fn.ValueKind == JsonValueKind.Object
                             && fn.TryGetProperty("name", out JsonElement fnName) && fnName.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(fnName.GetString()))
                    {
                        name = fnName.GetString()!.Trim();
                    }

                    result.Add((name, itemText));
                }
            }

            return result;
        }
        catch (JsonException)
        {
            return [(string.Empty, text)];
        }
    }

    private static List<string> InvocationsOf(IReadOnlyList<CanonicalRecord> records)
    {
        List<string> result = [];
        foreach (CanonicalRecord record in records)
        {
            if (!string.Equals(record.Op, "tool.invoke", StringComparison.Ordinal))
            {
                continue;
            }

            string name = (AttributeOf(record, ["gen_ai.tool.name", "tool.name", "tool_name"]) ?? record.Name).Trim();
            if (name.Length > 0 && !string.Equals(name, "tool.invoke", StringComparison.Ordinal))
            {
                result.Add(name);
            }
        }

        return result;
    }

    private static bool IsToolError(CanonicalRecord record)
    {
        if (string.Equals(record.Status, "error", StringComparison.Ordinal))
        {
            return true;
        }

        if (string.Equals(AttributeOf(record, ["gen_ai.tool.status", "status"]), "error", StringComparison.Ordinal))
        {
            return true;
        }

        if (record.Raw is not null)
        {
            if (record.Raw.TryGetValue("gen_ai.tool.status", out JsonElement st) && st.ValueKind == JsonValueKind.String && string.Equals(st.GetString(), "error", StringComparison.Ordinal))
            {
                return true;
            }

            if (record.Raw.TryGetValue("is_error", out JsonElement ie) && ie.ValueKind == JsonValueKind.True)
            {
                return true;
            }

            if (record.Raw.TryGetValue("isError", out JsonElement ie2) && ie2.ValueKind == JsonValueKind.True)
            {
                return true;
            }
        }

        return false;
    }

    private static List<string> ModelsOf(IReadOnlyList<CanonicalRecord> records)
    {
        HashSet<string> seen = new(StringComparer.Ordinal);
        List<string> list = [];
        foreach (CanonicalRecord record in records)
        {
            string? model = AttributeOf(record, ModelKeys);
            if (model is not null && seen.Add(model))
            {
                list.Add(model);
            }
        }

        return list;
    }

    private static (string Started, string Ended) TimestampBounds(IReadOnlyList<CanonicalRecord> records)
    {
        string? minTs = null;
        string? maxTs = null;
        double minMs = double.PositiveInfinity;
        double maxMs = double.NegativeInfinity;

        foreach (CanonicalRecord record in records)
        {
            double ms = RecordTimestampMs(record);
            if (!double.IsFinite(ms))
            {
                continue;
            }

            if (minTs is null || ms < minMs)
            {
                minTs = record.Timestamp;
                minMs = ms;
            }

            if (maxTs is null || ms >= maxMs)
            {
                maxTs = record.Timestamp;
                maxMs = ms;
            }
        }

        return (minTs ?? records[0].Timestamp, maxTs ?? records[^1].Timestamp);
    }

    private static string SessionLabelOf(
        IReadOnlyList<CanonicalRecord> records,
        string fallbackNamed,
        string harness,
        string sessionId)
    {
        string? bestLabel = null;
        double bestTime = double.NegativeInfinity;
        foreach (CanonicalRecord record in records)
        {
            string? label = AttributeOf(record, [SessionLabelKey])?.Trim();
            if (string.IsNullOrEmpty(label))
            {
                continue;
            }

            double time = RecordTimestampMs(record);
            bool later = bestLabel is null
                || (double.IsFinite(time) && (!double.IsFinite(bestTime) || time >= bestTime))
                || (!double.IsFinite(time) && !double.IsFinite(bestTime));
            if (later)
            {
                bestLabel = label;
                bestTime = time;
            }
        }

        return bestLabel ?? CanonPricing.FormatSessionTitle(new SessionTitleInput(
            Label: fallbackNamed,
            Harness: harness,
            SessionId: sessionId));
    }

    private static Dictionary<string, List<CanonicalRecord>> TurnsByRequestRoot(
        IReadOnlyList<CanonicalRecord> records,
        IReadOnlyList<CanonicalRecord> turnRecords)
    {
        Dictionary<string, CanonicalRecord> bySpan = new(StringComparer.Ordinal);
        foreach (CanonicalRecord r in records)
        {
            bySpan.TryAdd(r.SpanId, r);
        }

        Dictionary<string, string?> rootOf = new(StringComparer.Ordinal);
        string? Resolve(CanonicalRecord start)
        {
            List<string> path = [];
            HashSet<string> onPath = new(StringComparer.Ordinal);
            string? root = null;
            CanonicalRecord? cursor = start;
            while (cursor is not null)
            {
                if (rootOf.TryGetValue(cursor.SpanId, out string? known))
                {
                    root = known;
                    break;
                }

                if (!onPath.Add(cursor.SpanId))
                {
                    break;
                }

                path.Add(cursor.SpanId);
                if (cursor.ParentSpanId is null)
                {
                    root = cursor.SpanId;
                    break;
                }

                bySpan.TryGetValue(cursor.ParentSpanId, out cursor);
            }

            foreach (string id in path)
            {
                rootOf[id] = root;
            }

            return root;
        }

        Dictionary<string, List<CanonicalRecord>> owned = new(StringComparer.Ordinal);
        foreach (CanonicalRecord turn in turnRecords)
        {
            string? root = Resolve(turn);
            if (root is null)
            {
                continue;
            }

            if (!owned.TryGetValue(root, out List<CanonicalRecord>? list))
            {
                list = [];
                owned[root] = list;
            }

            list.Add(turn);
        }

        return owned;
    }

    private static bool HasNesting(IReadOnlyList<CanonicalRecord> records)
    {
        HashSet<string> ids = new(records.Select(static r => r.SpanId), StringComparer.Ordinal);
        return records.Any(r => r.ParentSpanId is not null && !string.Equals(r.ParentSpanId, r.SpanId, StringComparison.Ordinal) && ids.Contains(r.ParentSpanId));
    }

    private static string? PromptPreviewOf(CanonicalRecord record)
    {
        string? history = null;
        if (record.Parts is { Count: > 0 })
        {
            history = record.Parts
                .Where(static p => string.Equals(p.Part, "conversation_history", StringComparison.Ordinal) && !string.IsNullOrWhiteSpace(p.Text))
                .Select(static p => p.Text)
                .FirstOrDefault();
        }
        else if (record.Content.TryGetValue("conversation_history", out string? flat) && !string.IsNullOrWhiteSpace(flat))
        {
            history = flat;
        }

        if (history is null)
        {
            return null;
        }

        if (history.Length <= PromptPreviewLength)
        {
            return history;
        }

        int end = char.IsHighSurrogate(history[PromptPreviewLength - 1]) ? PromptPreviewLength - 1 : PromptPreviewLength;
        return history[..end];
    }

    private static Dictionary<string, List<CanonicalRecord>> GroupByCanonicalHarness(IReadOnlyList<CanonicalRecord> records)
    {
        Dictionary<string, List<CanonicalRecord>> groups = new(StringComparer.Ordinal);
        foreach (CanonicalRecord record in records)
        {
            string? id = CanonPricing.CanonicalHarnessId(record.Harness);
            if (id is null)
            {
                continue;
            }

            if (!groups.TryGetValue(id, out List<CanonicalRecord>? list))
            {
                list = [];
                groups[id] = list;
            }

            list.Add(record);
        }

        return groups;
    }

    private static bool HasEvidence(IReadOnlyList<CanonicalRecord> records) =>
        TurnsOf(records).Any(static r =>
            !string.Equals(CanonPricing.NormalizeHarnessName(r.Harness), "unattributed", StringComparison.Ordinal)
            || CanonPricing.MeasuredInput(r.Tokens) > 0
            || r.Tokens.Output > 0
            || (r.Parts is { Count: > 0 } && r.Parts.Any(static p => p.Text.Length > 0))
            || ((r.Parts is null || r.Parts.Count == 0) && r.Content.Count > 0));

    private static bool IsInvokeAgentSpan(CanonicalRecord record) =>
        string.Equals(record.Op, "agent.invoke", StringComparison.Ordinal)
        || string.Equals(AttributeOf(record, ["gen_ai.operation.name"]), "invoke_agent", StringComparison.Ordinal)
        || InvokeAgentNameRegex().IsMatch(record.Name)
        || string.Equals(AttributeOf(record, ["gen_ai.tool.name", "tool.name", "tool_name"]), "invoke_agent", StringComparison.Ordinal);

    private static List<CanonicalRecord> CollectDescendantRecords(string rootSpanId, IReadOnlyList<CanonicalRecord> records)
    {
        Dictionary<string, List<CanonicalRecord>> byParent = new(StringComparer.Ordinal);
        foreach (CanonicalRecord r in records)
        {
            if (string.IsNullOrEmpty(r.ParentSpanId))
            {
                continue;
            }

            if (!byParent.TryGetValue(r.ParentSpanId, out List<CanonicalRecord>? list))
            {
                list = [];
                byParent[r.ParentSpanId] = list;
            }

            list.Add(r);
        }

        List<CanonicalRecord> result = [];
        Queue<string> queue = new();
        HashSet<string> seen = new(StringComparer.Ordinal) { rootSpanId };
        queue.Enqueue(rootSpanId);

        while (queue.Count > 0)
        {
            string current = queue.Dequeue();
            if (!byParent.TryGetValue(current, out List<CanonicalRecord>? children))
            {
                continue;
            }

            foreach (CanonicalRecord child in children)
            {
                if (!seen.Add(child.SpanId))
                {
                    continue;
                }

                result.Add(child);
                if (!IsInvokeAgentSpan(child))
                {
                    queue.Enqueue(child.SpanId);
                }
            }
        }

        return result;
    }

    private static string? ExtractAgentName(CanonicalRecord invokeSpan)
    {
        string? attr = AttributeOf(invokeSpan, AgentNameKeys)?.Trim();
        if (!string.IsNullOrEmpty(attr))
        {
            return attr;
        }

        const string prefix = "invoke_agent ";
        if (invokeSpan.Name.StartsWith(prefix, StringComparison.Ordinal))
        {
            string candidate = invokeSpan.Name[prefix.Length..].Trim();
            if (candidate.Length > 0)
            {
                return candidate;
            }
        }

        return null;
    }

    private static string? ExtractTaskSummary(IReadOnlyList<CanonicalRecord> subagentRecords)
    {
        foreach (CanonicalRecord r in subagentRecords)
        {
            string? promptAttr = AttributeOf(r, ["gen_ai.prompt", "prompt", "task.description"]);
            if (!string.IsNullOrWhiteSpace(promptAttr))
            {
                return promptAttr.Trim();
            }

            string? preview = PromptPreviewOf(r);
            if (!string.IsNullOrWhiteSpace(preview))
            {
                return preview;
            }
        }

        return null;
    }

    private static bool IsClaudeHarness(string harness)
    {
        string norm = CanonPricing.NormalizeHarnessName(harness);
        return string.Equals(norm, "claude", StringComparison.Ordinal)
            || string.Equals(norm, "claude-code", StringComparison.Ordinal)
            || string.Equals(CanonPricing.SurveyFamily(harness), "claude-code", StringComparison.Ordinal);
    }

    private static string? ExtractClaudeAgentId(CanonicalRecord record)
    {
        string? fromAttr = AttributeOf(record, ["agent_id", "agentId", "subagent_id", "subagentId", "agent.id", "gen_ai.agent.id"])?.Trim();
        if (!string.IsNullOrEmpty(fromAttr))
        {
            return fromAttr;
        }

        return record.SpanId.StartsWith("agent-", StringComparison.Ordinal) ? record.SpanId : null;
    }

    private static string ClaudeSubagentSessionId(string parentSessionId, string agentId)
    {
        string trimmed = agentId.Trim();
        string bare = trimmed.StartsWith("agent-", StringComparison.Ordinal) ? trimmed["agent-".Length..] : trimmed;
        return $"{parentSessionId}#agent-{bare}";
    }

    private static string? ExtractClaudeAgentName(IReadOnlyList<CanonicalRecord> records)
    {
        foreach (CanonicalRecord record in records)
        {
            string? attr = AttributeOf(record, ["agent_type", "agentType", "gen_ai.agent.name", "gen_ai.agent.type", "agent_name", "agentName", "agent.name", "agent"])?.Trim();
            if (!string.IsNullOrEmpty(attr))
            {
                return attr;
            }
        }

        foreach (CanonicalRecord record in records)
        {
            string? name = ExtractAgentName(record);
            if (!string.IsNullOrEmpty(name))
            {
                return name;
            }
        }

        return null;
    }

    private static string? ExtractClaudeDescription(IReadOnlyList<CanonicalRecord> records)
    {
        foreach (CanonicalRecord record in records)
        {
            string? attr = AttributeOf(record, ["description", "task_description", "taskDescription", "task", "gen_ai.agent.description", "summary", "prompt"])?.Trim();
            if (!string.IsNullOrEmpty(attr))
            {
                return attr;
            }
        }

        return ExtractTaskSummary(records);
    }

    private static string? AttributeOf(CanonicalRecord record, IReadOnlyList<string> keys)
    {
        if (record.Raw is null)
        {
            return null;
        }

        foreach (string key in keys)
        {
            if (!record.Raw.TryGetValue(key, out JsonElement el))
            {
                continue;
            }

            if (el.ValueKind == JsonValueKind.String)
            {
                string? s = el.GetString();
                if (!string.IsNullOrEmpty(s))
                {
                    return s;
                }
            }
            else if (el.ValueKind == JsonValueKind.Number && el.TryGetDouble(out double d) && double.IsFinite(d))
            {
                return d.ToString(CultureInfo.InvariantCulture);
            }
        }

        return null;
    }

    private static bool IsNotMeasurableElement(IReadOnlyDictionary<string, JsonElement>? measurability, string key) =>
        measurability is not null
        && measurability.TryGetValue(key, out JsonElement el)
        && el.ValueKind == JsonValueKind.Object
        && el.TryGetProperty("availability", out JsonElement avail)
        && string.Equals(avail.GetString(), "not_measurable", StringComparison.Ordinal);

    private static bool IsMeasuredOrDerived(IReadOnlyDictionary<string, JsonElement>? measurability, string key) =>
        measurability is not null
        && measurability.TryGetValue(key, out JsonElement el)
        && el.ValueKind == JsonValueKind.String
        && (string.Equals(el.GetString(), "measured", StringComparison.Ordinal)
            || string.Equals(el.GetString(), "derived", StringComparison.Ordinal));

    private static bool TryUnavailable(
        IReadOnlyDictionary<string, JsonElement>? measurability,
        string key,
        out JsonElement element)
    {
        if (measurability is not null
            && measurability.TryGetValue(key, out JsonElement el)
            && el.ValueKind == JsonValueKind.Object
            && el.TryGetProperty("availability", out JsonElement avail)
            && string.Equals(avail.GetString(), "not_measurable", StringComparison.Ordinal))
        {
            element = el;
            return true;
        }

        element = default;
        return false;
    }

    private static object UnavailableOrValue(
        IReadOnlyDictionary<string, JsonElement>? measurability,
        IReadOnlyList<string> keys,
        long value)
    {
        foreach (string key in keys)
        {
            if (TryUnavailable(measurability, key, out JsonElement el))
            {
                return el;
            }
        }

        return value;
    }

    /// <summary>
    /// Runs an incremental relational session projection pass: rebuilds typed
    /// <see cref="StoredSessionProjection"/> rows (context, schema, timeline, turns, requests,
    /// and findings) for session keys dirtied since the previous pass, prunes stored projections
    /// whose records disappeared, and records the projection watermark.
    /// </summary>
    /// <returns>Summary of built, skipped, and pruned sessions plus emitted finding count.</returns>
    /// <remarks>
    /// <para>
    /// A relational projection row keys into the <c>session</c> table, so a session key whose
    /// canonical identity decorates its identifier (a harness-split key shared by two harnesses)
    /// is skipped rather than written: the relational ingest schema does not yet model
    /// harness-split or subagent session rows. Subagent and auxiliary involvement is recorded as
    /// timeline marks on the parent row instead.
    /// </para>
    /// <para>
    /// Until ingest wiring advances <c>store_write_marker</c> on writes, the watermark comparison
    /// alone cannot observe new records; callers therefore request a projection after ingest, the
    /// same flow the projection service already uses.
    /// </para>
    /// </remarks>
    private ProjectionPassReport ProjectIncrementalRelational()
    {
        _ = pricing.LoadPricingTables();

        long currentMarker = operationalStore!.GetWriteMarker();
        ProjectionStateView? previousState = operationalStore.GetProjectionState();
        DirtyProjectionState? dirtyState = operationalStore.GetProjectionDirtyState();
        bool environmentCurrent = HasCurrentProjectionEnvironment();

        bool canIncremental = environmentCurrent
            && previousState is not null
            && dirtyState is not null
            && dirtyState.FromSeq == previousState.RecordsMarker
            && dirtyState.ToSeq == currentMarker
            && !dirtyState.IsWildcard
            && dirtyState.Keys is not null;

        HashSet<string>? dirtySet = canIncremental
            ? new HashSet<string>(dirtyState!.Keys!, StringComparer.Ordinal)
            : null;

        Dictionary<string, CollectedSession> sessionsByKey = new(StringComparer.Ordinal);
        foreach (CollectedSession session in reader!.Sessions())
        {
            sessionsByKey.TryAdd(session.SessionKey, session);
        }

        Dictionary<string, List<CollectedRecord>> collectedByKey = new(StringComparer.Ordinal);
        foreach (CollectedRecord record in reader.Records())
        {
            if (string.IsNullOrEmpty(record.SessionKey))
            {
                continue;
            }

            if (!collectedByKey.TryGetValue(record.SessionKey!, out List<CollectedRecord>? list))
            {
                list = [];
                collectedByKey[record.SessionKey!] = list;
            }

            list.Add(record);
        }

        List<string> sessionKeys = [.. collectedByKey.Keys.OrderBy(static key => key, StringComparer.Ordinal)];
        SessionIdentities identities = new(
            sessionKeys.SelectMany(key => collectedByKey[key].Select(record => (key, record.HarnessId))));

        HashSet<string> storedProjectionIds = new(
            projections!.ListSessionProjections().Select(static projection => projection.SessionId),
            StringComparer.Ordinal);

        CachedTokenCounter counter = new(operationalStore, CanonTokenCache.ApproximateTokenizerName);

        int builtCount = 0;
        int skippedCount = 0;
        int findingsCount = 0;
        List<string> rebuiltKeys = [];
        List<string> skippedKeys = [];
        HashSet<string> builtSessionIds = new(StringComparer.Ordinal);

        foreach (string key in sessionKeys)
        {
            if (canIncremental
                && dirtySet is not null
                && !dirtySet.Contains(key)
                && PriorIdsOf(key, collectedByKey[key], identities).All(storedProjectionIds.Contains))
            {
                skippedKeys.Add(key);
                skippedCount++;
                continue;
            }

            List<CanonicalRecord> hydrated = [.. collectedByKey[key].Select(HydrateRecord)];
            hydrated.Sort(CompareRecordTimestamps);
            Dictionary<string, List<CanonicalRecord>> grouped = GroupByCanonicalHarness(hydrated);
            if (grouped.Count == 0)
            {
                skippedCount++;
                continue;
            }

            foreach (KeyValuePair<string, List<CanonicalRecord>> group in grouped)
            {
                string harness = group.Key;
                List<CanonicalRecord> groupRecords = group.Value;
                if (groupRecords.Count == 0 || !HasEvidence(groupRecords))
                {
                    skippedCount++;
                    continue;
                }

                IReadOnlyList<CanonicalRecord> merged = DedupeTwinTurns(groupRecords, key);
                if (merged.Count == 0 || !HasEvidence(merged))
                {
                    skippedCount++;
                    continue;
                }

                string sessionId = identities.Claim(key, harness);
                if (!sessionsByKey.ContainsKey(sessionId))
                {
                    skippedCount++;
                    continue;
                }

                StoredSessionProjection projection = BuildStoredSessionProjection(
                    sessionId,
                    merged,
                    counter.Count,
                    sessionsByKey[sessionId].Title);
                projections.ReplaceSessionProjection(projection);
                builtSessionIds.Add(sessionId);
                builtCount++;

                findingsCount += RebuildSessionFindingsRelational(sessionId, merged, identities);
            }

            rebuiltKeys.Add(key);
        }

        counter.Flush();

        int prunedCount = 0;
        foreach (StoredSessionProjection stored in projections.ListSessionProjections())
        {
            if (builtSessionIds.Contains(stored.SessionId))
            {
                continue;
            }

            projections.DeleteSessionProjection(stored.SessionId);
            projections.DeleteFindingsForSession(stored.SessionId);
            prunedCount++;
        }

        long finalMarker = operationalStore.GetWriteMarker();
        operationalStore.SetProjectionState(new ProjectionStateView(CanonSchema.SchemaVersion, finalMarker));
        operationalStore.ClearProjectionDirtyState();

        return new ProjectionPassReport(
            Built: builtCount,
            Skipped: skippedCount,
            Pruned: prunedCount,
            Findings: findingsCount,
            RebuiltKeys: rebuiltKeys,
            SkippedKeys: skippedKeys);
    }

    private static IEnumerable<string> PriorIdsOf(
        string key,
        IReadOnlyList<CollectedRecord> records,
        SessionIdentities identities)
    {
        HashSet<string> harnesses = new(records.Select(static record => record.HarnessId), StringComparer.Ordinal);
        foreach (string harness in harnesses)
        {
            string? id = identities.IdFor(key, harness);
            if (id is not null)
            {
                yield return id;
            }
        }
    }

    private int RebuildSessionFindingsRelational(
        string sessionId,
        IReadOnlyList<CanonicalRecord> sessionRecords,
        SessionIdentities identities)
    {
        projections!.DeleteFindingsForSession(sessionId);
        OutcomeBlock outcome = CanonFindings.DeriveOutcome(sessionRecords);
        IReadOnlyList<Finding> findings = CanonFindings.DetectFindings(new DetectFindingsInput(
            RunId: sessionId,
            SessionId: sessionId,
            Records: sessionRecords,
            Outcome: outcome,
            SessionIdentityLookup: identities.IdFor));
        if (findings.Count > 0)
        {
            projections.ReplaceFindings(findings.Select(ToStoredFinding));
        }

        return findings.Count;
    }

    private static StoredFinding ToStoredFinding(Finding finding)
    {
        StoredCompactionHazard? hazard = null;
        if (finding.Payload is not null
            && finding.Payload.TryGetValue("contextLimit", out object? limitValue)
            && finding.Payload.TryGetValue("contextLimitSource", out object? sourceValue)
            && limitValue is long limit
            && sourceValue is string limitSource)
        {
            hazard = new StoredCompactionHazard(limit, limitSource);
        }

        return new StoredFinding(
            FindingId: finding.Id,
            DetectorId: finding.DetectorId,
            Title: finding.Title,
            Mechanism: finding.Mechanism,
            Confidence: finding.Confidence,
            Recommendation: finding.Recommendation,
            OutcomeRiskCaveat: finding.OutcomeRiskCaveat,
            RankScore: finding.RankScore,
            EvidenceLinks: finding.EvidenceLinks,
            EstimatedWasteTokens: finding.EstimatedWasteTokens,
            ErrorBar: finding.ErrorBar,
            RunId: finding.RunId,
            SessionId: finding.SessionId,
            MeasurementClass: finding.MeasurementClass,
            ConfidenceBasis: finding.ConfidenceBasis,
            WhatWouldRaiseIt: finding.WhatWouldRaiseIt,
            CompactionHazard: hazard);
    }

    /// <summary>
    /// Rebuilds one typed <see cref="StoredSessionProjection"/> from a session's deduplicated
    /// records: context pressure analysis, schema ranking, timeline marks, requests, and turns,
    /// with exclusive unmeasurable reasons in place of non-measurable figures.
    /// </summary>
    /// <param name="sessionId">Canonical session identifier (a <c>session</c> table key).</param>
    /// <param name="records">Deduplicated session records in timestamp order.</param>
    /// <param name="countTokens">Token counting callback.</param>
    /// <param name="title">Session title from the relational <c>session</c> row, when present.</param>
    /// <returns>Projected typed session row.</returns>
    private static StoredSessionProjection BuildStoredSessionProjection(
        string sessionId,
        IReadOnlyList<CanonicalRecord> records,
        Func<string, long> countTokens,
        string? title = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentNullException.ThrowIfNull(records);
        ArgumentNullException.ThrowIfNull(countTokens);
        if (records.Count == 0)
        {
            throw new ArgumentException("At least one record is required to build a session projection.", nameof(records));
        }

        DroidSessionSplit split = SplitDroidSessionRecords(records);
        bool paired = split.TurnRecords.Count > 0 && split.CounterRecords.Count > 0;
        List<CanonicalRecord> turnRecords = paired ? TurnsOf(split.TurnRecords) : TurnsOf(records);
        IReadOnlyList<CanonicalRecord> structureRecords = paired ? split.TurnRecords : records;

        CanonicalRecord first = records[0];
        string harness = CanonPricing.NormalizeHarnessName(first.Harness);

        List<CanonicalRecord> mainThreadRecords = turnRecords.Where(static record => !IsSideAgentTurn(record)).ToList();
        List<CanonicalRecord> contextRecords = mainThreadRecords.Count > 0 ? mainThreadRecords : turnRecords;
        List<CanonicalRecord> measuredTurns = contextRecords.Where(static record => CanonPricing.MeasuredInput(record.Tokens) > 0).ToList();

        List<ContextTurn> contextTurns = new(measuredTurns.Count);
        List<ContextPart> lastSystem = [];
        foreach (CanonicalRecord record in measuredTurns)
        {
            List<ContextPart> parts = PartsOf(record);
            List<ContextPart> ownSystem = parts.Where(static part => string.Equals(part.Part, "system_prompt", StringComparison.Ordinal)).ToList();
            if (ownSystem.Count > 0)
            {
                lastSystem = ownSystem;
            }

            IReadOnlyList<ContextPart> effective = ownSystem.Count > 0 ? parts : [.. lastSystem, .. parts];
            contextTurns.Add(new ContextTurn(
                Parts: effective,
                InputTokens: CanonPricing.MeasuredInput(record.Tokens),
                FreshInput: record.Tokens.FreshInput));
        }

        IReadOnlyList<CanonicalRecord> windowRecords = contextRecords.Count > 0 ? contextRecords : records;
        ContextWindowResult window = CanonPricing.ContextLimitOf(windowRecords);
        long analysisLimit = window.ContextLimit > 0 ? window.ContextLimit : CanonPricing.DefaultContextLimit;

        ContextAnalysis context = CanonContextAnalysis.AnalyzeContext(
            contextTurns,
            new ContextCountOptions(
                ContextLimit: analysisLimit,
                ContextLimitSource: window.ContextLimitSource,
                CountTokens: countTokens));

        List<ToolDefinition> definitions = [.. TypedToolDefinitionsOf(records)];
        HashSet<string> knownDefinitions = [.. definitions.Select(static definition => definition.Name)];
        IReadOnlyList<CanonicalRecord> toolSourceRecords = paired && split.FileToolRecords.Count > 0
            ? split.FileToolRecords
            : structureRecords;
        definitions.AddRange(ToolDefinitionsOf(records, countTokens)
            .Where(definition => knownDefinitions.Add(definition.Name)));
        SchemaCostAnalysis schema = CanonContextAnalysis.RankSchemas(
            definitions,
            turnRecords.Count,
            InvocationsOf(toolSourceRecords));

        TimelineNode timeline = CanonContextAnalysis.BuildTimeline(structureRecords);

        List<CanonicalRecord> requestRoots = structureRecords.Where(static record => record.ParentSpanId is null).ToList();
        (string started, string ended) = TimestampBounds(records);

        string? resolvedAgentName = AttributeOf(first, AgentNameKeys);
        string resolvedLabel = SessionLabelOf(records, title ?? first.Name, harness, sessionId);
        string? parentAttr = AttributeOf(first, ParentSessionKeys);
        bool resolvedIsSubagent = parentAttr is not null || AttributeOf(first, ["gen_ai.is_subagent", "is_subagent"]) is not null;

        List<StoredSessionTurn> turns = new(turnRecords.Count);
        for (int position = 0; position < turnRecords.Count; position++)
        {
            turns.Add(new StoredSessionTurn(sessionId, position, turnRecords[position].SpanId));
        }

        return new StoredSessionProjection(
            SessionId: sessionId,
            Label: resolvedLabel,
            IsSubagent: resolvedIsSubagent,
            SpanCount: records.Count,
            StartedAtUtc: started,
            EndedAtUtc: ended,
            ParentSessionId: parentAttr,
            AgentName: resolvedAgentName,
            IncludedInParent: false,
            RequestSpanIds: [.. requestRoots.Select(static record => record.SpanId)],
            Turns: turns,
            Context: context.Measurable ? ToStoredContext(context) : null,
            ContextUnmeasurableReason: context.Measurable ? null : context.Reason ?? NoMessageStructureReason,
            Schema: schema.Measurable ? ToStoredSchema(schema) : null,
            SchemaUnmeasurableReason: schema.Measurable ? null : schema.Reason ?? NoToolDefinitionSchemaReason,
            TimelineSubagentSpanIds: TimelineSubagentSpanIdsOf(timeline),
            TimelineAuxiliarySpanIds: TimelineAuxiliarySpanIdsOf(timeline));
    }

    private static StoredSessionContext ToStoredContext(ContextAnalysis context)
    {
        List<StoredSessionContextTurn> turns = new(context.Turns.Count);
        foreach (TurnPressure turn in context.Turns)
        {
            turns.Add(new StoredSessionContextTurn(
                Position: turn.Index - 1,
                InputTokens: turn.InputTokens,
                BucketedTokens: turn.BucketedTokens,
                ResidualTokens: turn.Residual.Tokens,
                ResidualAttribution: turn.Residual.Attribution,
                BuiltinToolDefinitionTokens: turn.BuiltinToolDefinitionTokens,
                FreshInput: turn.FreshInput,
                Buckets: [.. turn.Buckets
                    .Where(static entry => entry.Value != 0)
                    .OrderBy(static entry => entry.Key, StringComparer.Ordinal)
                    .Select(static entry => new StoredTurnBucketTokens(entry.Key, entry.Value))],
                ServerDefinitions: [.. turn.ToolDefinitionsByServer
                    .OrderBy(static entry => entry.Key, StringComparer.Ordinal)
                    .Select(static entry => new StoredTurnServerDefinitionTokens(entry.Key, entry.Value))],
                Headroom: turn.Headroom,
                Pressure: turn.Pressure,
                AccumulationRate: turn.AccumulationRate,
                StrippedBlockCount: turn.StrippedInstructionBlocks.Count,
                StrippedTokens: turn.StrippedInstructionBlocks.Tokens,
                PreviousFreshInput: turn.FreshInputJump?.Previous,
                FreshJumpFactor: turn.FreshInputJump is null
                    ? null
                    : double.IsFinite(turn.FreshInputJump.Factor) ? turn.FreshInputJump.Factor : null,
                NonResidentTokens: turn.NonResidentToolDefinitions));
        }

        return new StoredSessionContext(
            ContextLimit: context.ContextLimit,
            ContextLimitSource: context.ContextLimitSource ?? DefaultContextLimitSource,
            ResidualTotal: context.ResidualTotal,
            FreshJumpFactor: context.FreshJumpFactor,
            AccumulationRate: context.SessionAccumulationRate,
            ObservedBuckets: context.ObservedParts,
            // The analysis reports only whether some counts were derived; it does not expose
            // per-counter attribution, so no derived-count rows can be named honestly here.
            DerivedCounts: [],
            FlaggedTurnIndexes: [.. context.FlaggedTurns.Select(static index => index - 1)],
            Turns: turns);
    }

    private static StoredSessionSchema ToStoredSchema(SchemaCostAnalysis schema)
    {
        List<StoredSchemaRankedTool> rankedTools = new(schema.Ranked.Count);
        for (int position = 0; position < schema.Ranked.Count; position++)
        {
            RankedTool tool = schema.Ranked[position];
            rankedTools.Add(new StoredSchemaRankedTool(
                Position: position,
                ToolName: tool.Name,
                CostTokens: tool.Cost,
                Invoked: tool.Invoked,
                ServerName: tool.Server));
        }

        return new StoredSessionSchema(
            InvocationCount: schema.InvocationCount,
            TurnCount: schema.Turns,
            RankedTools: rankedTools,
            Servers: [.. schema.ByServer.Select(static entry => new StoredSchemaServerTokens(entry.Key, entry.Value))],
            UnusedTokenResidencies: schema.UnusedRange?.TokenResidencies);
    }

    private static IReadOnlyList<string> TimelineSubagentSpanIdsOf(TimelineNode root)
    {
        List<string> spanIds = [];
        void Walk(TimelineNode node)
        {
            if (node.IsSubagent && !IsSyntheticTimelineId(node.SpanId))
            {
                spanIds.Add(node.SpanId);
            }

            foreach (TimelineNode child in node.Children)
            {
                Walk(child);
            }
        }

        Walk(root);
        return spanIds;
    }

    private static IReadOnlyList<string> TimelineAuxiliarySpanIdsOf(TimelineNode root)
    {
        List<string> spanIds = [];
        void Walk(TimelineNode node)
        {
            if (node.IsAuxiliary && !IsSyntheticTimelineId(node.SpanId))
            {
                spanIds.Add(node.SpanId);
            }

            foreach (TimelineNode child in node.Children)
            {
                Walk(child);
            }
        }

        Walk(root);
        return spanIds;
    }

    private static bool IsSyntheticTimelineId(string spanId) => spanId.StartsWith('(');

    /// <summary>
    /// Collects the typed tool definitions carried on hydrated records: each relational
    /// <c>tool_definition</c> row hydrates as a <c>tool_definitions</c> part whose text is the
    /// tool name and whose token count is the measured <c>schema_tokens</c>.
    /// </summary>
    /// <param name="records">Hydrated session records.</param>
    /// <returns>Distinct tool definitions with residency counts.</returns>
    private static List<ToolDefinition> TypedToolDefinitionsOf(IReadOnlyList<CanonicalRecord> records)
    {
        Dictionary<string, (string? Server, long Tokens, int TurnsResident)> byName = new(StringComparer.Ordinal);
        foreach (CanonicalRecord record in records)
        {
            HashSet<string> seenInRecord = new(StringComparer.Ordinal);
            foreach (ContentPart part in record.Parts ?? [])
            {
                if (!string.Equals(part.Part, "tool_definitions", StringComparison.Ordinal) || part.Tokens is not > 0)
                {
                    continue;
                }

                string name = part.Text.Trim();
                if (name.Length == 0 || !seenInRecord.Add(name))
                {
                    continue;
                }

                if (byName.TryGetValue(name, out (string? Server, long Tokens, int TurnsResident) existing))
                {
                    byName[name] = (existing.Server ?? part.Server, Math.Max(existing.Tokens, part.Tokens.Value), existing.TurnsResident + 1);
                }
                else
                {
                    byName[name] = (part.Server, part.Tokens.Value, 1);
                }
            }
        }

        return [.. byName.Select(static entry => new ToolDefinition(entry.Key, entry.Value.Server, entry.Value.Tokens, entry.Value.TurnsResident))];
    }

    /// <summary>
    /// Hydrates one relational <see cref="CollectedRecord"/> into the canonical analysis record:
    /// typed token, part, tool-definition, agent, and declared-window columns become the token
    /// usage, content parts, and minimal raw attributes the analysis pipeline reads.
    /// </summary>
    /// <param name="record">Relational record read through <see cref="IRelationalStoreReader"/>.</param>
    /// <returns>Hydrated canonical record for the shared analysis pipeline.</returns>
    private static CanonicalRecord HydrateRecord(CollectedRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        Dictionary<string, JsonElement> raw = new(StringComparer.Ordinal);
        if (record.Signals.DeclaredContextWindow is > 0)
        {
            raw[CanonPricing.DeclaredContextLimitKey] = JsonSerializer.SerializeToElement(record.Signals.DeclaredContextWindow.Value);
        }

        if (!string.IsNullOrWhiteSpace(record.Model))
        {
            raw["gen_ai.response.model"] = JsonSerializer.SerializeToElement(record.Model);
        }

        if (!string.IsNullOrWhiteSpace(record.Agent?.AgentName))
        {
            raw["gen_ai.agent.name"] = JsonSerializer.SerializeToElement(record.Agent.AgentName);
        }

        long freshInput = 0;
        long cacheRead = 0;
        long cacheCreation = 0;
        long output = 0;
        long? reasoning = null;
        foreach (CollectedTokenCount tokenCount in record.Tokens)
        {
            switch (tokenCount.TokenClass)
            {
                case TokenClass.Input:
                    freshInput = tokenCount.Count;
                    break;
                case TokenClass.CacheRead:
                    cacheRead = tokenCount.Count;
                    break;
                case TokenClass.CacheWrite:
                    cacheCreation = tokenCount.Count;
                    break;
                case TokenClass.Output:
                    output = tokenCount.Count;
                    break;
                case TokenClass.Reasoning:
                    reasoning = tokenCount.Count;
                    break;
            }
        }

        List<ContentPart> parts = new(record.Parts.Count + record.ToolDefinitions.Count);
        foreach (CollectedContentPart part in record.Parts)
        {
            parts.Add(new ContentPart(
                Part: PartKeyOf(part.Bucket),
                Text: part.PlainText ?? string.Empty,
                Tokens: part.ReportedTokens ?? part.Withheld?.MeasuredTokens,
                Server: part.Server,
                Role: part.Role,
                Order: part.Position,
                Resident: part.Resident,
                Truncated: part.OriginalUtf8BytesWhenTruncated.HasValue));
        }

        foreach (CollectedToolDefinition definition in record.ToolDefinitions)
        {
            parts.Add(new ContentPart(
                Part: "tool_definitions",
                Text: definition.ToolName,
                Tokens: definition.SchemaTokens,
                Server: definition.Server,
                Order: TypedDefinitionOrderBase + definition.Position));
        }

        parts.Sort(static (left, right) => (left.Order ?? 0).CompareTo(right.Order ?? 0));

        return new CanonicalRecord
        {
            SpanId = record.RecordId,
            TraceId = record.TraceId,
            ParentSpanId = record.ParentRecordId,
            Source = RelationalSourceName,
            Harness = record.HarnessId,
            SessionId = record.SessionKey,
            Name = record.Name,
            Op = OpNameOf(record.Op),
            Kind = KindNameOf(record.Kind),
            Timestamp = record.StartedAt.UtcDateTime.ToString(TimestampFormat, CultureInfo.InvariantCulture),
            DurationMs = record.ReportedDurationMs ?? 0,
            Status = StatusNameOf(record.Status),
            Tokens = new TokenUsage(
                FreshInput: freshInput,
                CacheRead: cacheRead,
                CacheCreation: cacheCreation,
                Output: output,
                ReportedInput: record.ReportedInputTokens ?? 0,
                ReportedOutput: record.ReportedOutputTokens ?? 0,
                Reasoning: reasoning),
            Content = CanonValidation.ContentFromParts(parts),
            Parts = parts,
            Cost = record.ReportedCost is null
                ? new CostBlock(CostBasis.Unknown, CostStatus.NoRate)
                : new CostBlock(CostBasis.Harness, CostStatus.Priced, record.ReportedCost.Amount, record.ReportedCost.Currency),
            Measurability = null,
            Raw = raw,
        };
    }

    private static string OpNameOf(RecordOp op) => op switch
    {
        RecordOp.LlmInvoke => "llm.invoke",
        RecordOp.ToolInvoke => "tool.invoke",
        RecordOp.AgentInvoke => "agent.invoke",
        _ => "unspecified",
    };

    private static string KindNameOf(RecordKind kind) => kind switch
    {
        RecordKind.Internal => "internal",
        RecordKind.Server => "server",
        RecordKind.Client => "client",
        RecordKind.Producer => "producer",
        RecordKind.Consumer => "consumer",
        _ => "unspecified",
    };

    private static string StatusNameOf(RecordStatus status) => status switch
    {
        RecordStatus.Ok => "ok",
        RecordStatus.Error => "error",
        RecordStatus.Unspecified => "unspecified",
        _ => "unset",
    };

    private static string PartKeyOf(ContentBucket bucket) => bucket switch
    {
        ContentBucket.SystemPrompt => "system_prompt",
        ContentBucket.ToolDefinitions => "tool_definitions",
        ContentBucket.ToolDefinition => "tool_definition",
        ContentBucket.InstructionContext => "instruction_context",
        ContentBucket.ConversationHistory => "conversation_history",
        ContentBucket.ToolResultContent => "tool_result_content",
        ContentBucket.UserPrompt => "user_prompt",
        ContentBucket.UserMessage => "user_message",
        _ => "reasoning",
    };

    private sealed record DroidSessionSplit(
        List<CanonicalRecord> TurnRecords,
        List<CanonicalRecord> CounterRecords,
        List<CanonicalRecord> FileToolRecords);

    private class SessionIdentities
    {
        private readonly Dictionary<string, Dictionary<string, string>> ids = new(StringComparer.Ordinal);
        private readonly HashSet<string> assignedSessionIds = new(StringComparer.Ordinal);

        public SessionIdentities(IEnumerable<(string Key, string Harness)> pairs)
        {
            Dictionary<string, HashSet<string>> harnessesByKey = new(StringComparer.Ordinal);
            foreach ((string key, string rawHarness) in pairs)
            {
                string? harness = CanonPricing.CanonicalHarnessId(rawHarness);
                if (harness is null)
                {
                    continue;
                }

                if (!harnessesByKey.TryGetValue(key, out HashSet<string>? harnesses))
                {
                    harnesses = new HashSet<string>(StringComparer.Ordinal);
                    harnessesByKey[key] = harnesses;
                }

                harnesses.Add(harness);
            }

            List<(string Key, List<string> Harnesses)> split = [];
            foreach (KeyValuePair<string, HashSet<string>> kv in harnessesByKey)
            {
                if (kv.Value.Count == 1)
                {
                    Assign(kv.Key, kv.Value.First(), kv.Key);
                }
                else
                {
                    split.Add((kv.Key, kv.Value.OrderBy(static h => h, StringComparer.Ordinal).ToList()));
                }
            }

            split.Sort(static (a, b) => string.CompareOrdinal(a.Key, b.Key));
            foreach ((string key, List<string> harnesses) in split)
            {
                foreach (string harness in harnesses)
                {
                    Claim(key, harness);
                }
            }
        }

        public string? IdFor(string key, string harness)
        {
            string canonical = CanonPricing.NormalizeHarnessName(harness);
            return ids.TryGetValue(key, out Dictionary<string, string>? byHarness)
                && byHarness.TryGetValue(canonical, out string? id)
                ? id
                : null;
        }

        public string Claim(string key, string harness)
        {
            string canonical = CanonPricing.NormalizeHarnessName(harness);
            string? existing = IdFor(key, canonical);
            if (existing is not null)
            {
                return existing;
            }

            string candidate = $"{canonical}:{key}";
            while (assignedSessionIds.Contains(candidate))
            {
                candidate = $"{canonical}:{candidate}";
            }

            Assign(key, canonical, candidate);
            return candidate;
        }

        private void Assign(string key, string harness, string id)
        {
            if (!ids.TryGetValue(key, out Dictionary<string, string>? byHarness))
            {
                byHarness = new Dictionary<string, string>(StringComparer.Ordinal);
                ids[key] = byHarness;
            }

            byHarness[harness] = id;
            assignedSessionIds.Add(id);
        }
    }
}
