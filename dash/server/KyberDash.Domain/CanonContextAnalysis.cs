using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace KyberDash.Domain;

/// <summary>One piece of a turn's context payload keyed by canonical part type.</summary>
public record ContextPart(
    string Part,
    string Text,
    long? Tokens = null,
    string? Server = null,
    bool Derived = false,
    long? InstructionTokens = null,
    int? StrippedCount = null,
    bool? Resident = null)
{
    /// <summary>Adapts a persisted <see cref="ContentPart"/> into an analysis <see cref="ContextPart"/>.</summary>
    public static ContextPart FromContentPart(ContentPart part)
    {
        ArgumentNullException.ThrowIfNull(part);
        return new ContextPart(
            Part: part.Part,
            Text: part.Text,
            Tokens: part.Tokens,
            Server: part.Server,
            Resident: part.Resident);
    }
}

/// <summary>Single turn of a session supplied to <see cref="CanonContextAnalysis.AnalyzeContext"/>.</summary>
public record ContextTurn(
    IReadOnlyList<ContextPart> Parts,
    long InputTokens,
    long? FreshInput);

/// <summary>Delimiters separating embedded instruction blocks from carrier content.</summary>
public record InstructionBlockMarkers(string Open, string Close);

/// <summary>Options controlling context composition and pressure analysis.</summary>
public record ContextCountOptions(
    long ContextLimit,
    string? ContextLimitSource = null,
    double FreshJumpFactor = CanonContextAnalysis.DefaultFreshJumpFactor,
    long ZeroPreviousFloor = CanonContextAnalysis.DefaultZeroPreviousFloor,
    InstructionBlockMarkers? InstructionMarkers = null,
    Func<string, long>? CountTokens = null,
    IReadOnlyDictionary<string, JsonElement>? Measurability = null);

/// <summary>Count and tokens of instruction blocks stripped from carrier buckets in one turn.</summary>
public record StrippedInstructionSummary(int Count, long Tokens);

/// <summary>Unaccounted gap between measured input tokens and bucketed content tokens.</summary>
public record TurnResidual(long Tokens, string Attribution);

/// <summary>Recorded sharp fresh-input jump between consecutive turns.</summary>
public record FreshInputJump(long Previous, double Factor);

/// <summary>Context composition, headroom, pressure, and residual metrics for one turn.</summary>
public record TurnPressure(
    int Index,
    long InputTokens,
    IReadOnlyDictionary<string, long> Buckets,
    IReadOnlyDictionary<string, long> ToolDefinitionsByServer,
    long BuiltinToolDefinitionTokens,
    StrippedInstructionSummary StrippedInstructionBlocks,
    long BucketedTokens,
    TurnResidual Residual,
    long Headroom,
    double Pressure,
    long AccumulationRate,
    long? FreshInput,
    FreshInputJump? FreshInputJump = null,
    int? NonResidentToolDefinitions = null);

/// <summary>Session-level context composition and pressure analysis result.</summary>
public record ContextAnalysis(
    bool Measurable,
    string? Reason,
    long ContextLimit,
    string? ContextLimitSource,
    int TurnCount,
    IReadOnlyList<TurnPressure> Turns,
    IReadOnlyList<string> ObservedParts,
    long ResidualTotal,
    bool DerivedCounts,
    double FreshJumpFactor,
    IReadOnlyList<int> FlaggedTurns,
    double SessionAccumulationRate);

/// <summary>Tool definition entry supplied to <see cref="CanonContextAnalysis.RankSchemas"/>.</summary>
public record ToolDefinition(
    string Name,
    string? Server = null,
    long Tokens = 0,
    int? TurnsResident = null);

/// <summary>One tool definition ordered in the schema-cost ranking.</summary>
public record RankedTool(
    string Name,
    string? Server,
    long Cost,
    bool Invoked);

/// <summary>Per-million cache-read and fresh-input rates used to bound unused schema cost.</summary>
public record SchemaCostRates(
    double CacheReadRate,
    double FreshInputRate,
    string? Currency = null);

/// <summary>Lower/upper cost bounds on tool schemas offered across a session and never invoked.</summary>
public record UnusedSchemaRange(
    long TokenResidencies,
    double Floor,
    double Ceiling,
    string? Currency = null);

/// <summary>Result of ranking tool schemas and bounding unused schema rent.</summary>
public record SchemaCostAnalysis(
    bool Measurable,
    int InvocationCount,
    string? Reason,
    IReadOnlyList<RankedTool> Ranked,
    IReadOnlyList<RankedTool> NeverInvoked,
    IReadOnlyDictionary<string, long> ByServer,
    UnusedSchemaRange? UnusedRange,
    int Turns);

/// <summary>Node in the hierarchical execution timeline tree built from canonical spans.</summary>
public class TimelineNode
{
    /// <summary>Span identifier (or synthetic parenthesized container identifier such as <c>(session)</c>).</summary>
    public required string SpanId { get; init; }

    /// <summary>Declared parent span identifier from the underlying record, or <see langword="null"/>.</summary>
    public string? ParentId { get; init; }

    /// <summary>Mutable collection of child nodes ordered chronologically then by span ID.</summary>
    public Collection<TimelineNode> Children { get; } = [];

    /// <summary>Milliseconds elapsed from session start to this node's start.</summary>
    public long StartMs { get; set; }

    /// <summary>Duration of this span or spanning container in milliseconds.</summary>
    public long DurationMs { get; set; }

    /// <summary>Normalized span kind (<c>"internal"</c>, <c>"synthetic"</c>, etc.).</summary>
    public required string Kind { get; init; }

    /// <summary>Display name of the span or synthetic group.</summary>
    public required string Name { get; init; }

    /// <summary>Optional normalized status code.</summary>
    public string? Status { get; init; }

    /// <summary>Raw harness attributes preserved on this span (empty on synthetic nodes).</summary>
    public required IReadOnlyDictionary<string, JsonElement> Attributes { get; init; }

    /// <summary>True when span name/op/non-<c>kyber.*</c> attributes indicate subagent activity.</summary>
    public bool IsSubagent { get; init; }

    /// <summary>True when the span is partitioned under <c>(auxiliary)</c>.</summary>
    public bool IsAuxiliary { get; set; }

    /// <summary>Cost block of the span or totaled subtree.</summary>
    public required CostBlock Cost { get; set; }
}

/// <summary>Identified subagent invocation node together with the parent span that spawned it.</summary>
public record SubagentSession(
    string SpanId,
    string? ParentSpanId,
    string Name);

/// <summary>
/// Context composition, schema-cost ranking, and timeline hierarchy analyses mirroring
/// <c>dash/src/analysis/context.ts</c>, <c>dash/src/analysis/schema.ts</c>, and
/// <c>dash/src/analysis/timeline.ts</c>.
/// </summary>
public static class CanonContextAnalysis
{
    /// <summary>Default multiplier flagging sharp fresh-input increases across consecutive turns.</summary>
    public const double DefaultFreshJumpFactor = 2.0;

    /// <summary>Fresh-input floor above which a jump from zero previous fresh input is flagged.</summary>
    public const long DefaultZeroPreviousFloor = 1_000L;

    /// <summary>Synthetic root identifier for the session timeline tree.</summary>
    public const string SessionRootId = "(session)";

    /// <summary>Synthetic container identifier for auxiliary non-conversation spans.</summary>
    public const string AuxiliaryGroupId = "(auxiliary)";

    private const string SyntheticKind = "synthetic";
    private const string KyberAttributePrefix = "kyber.";
    private const string ResidualTokenizerDrift = "tokenizer_drift";
    private const string ResidualUnattributed = "unattributed";

    private static readonly InstructionBlockMarkers DefaultInstructionMarkers =
        new("<system-reminder>", "</system-reminder>");

    private static readonly SchemaCostRates DefaultTokenResidencyRates =
        new(0, CanonPricing.TokensPerMillion);

    private static readonly string[] SubagentMarkers = ["subagent", "sub-agent", "sub_agent"];
    private static readonly string[] AuxiliaryMarkers = ["title", "xtabprovider"];

    private static readonly CostBlock UnpricedCost = new(CostBasis.Unknown, CostStatus.NoRate);

    /// <summary>
    /// Counts and releases heavy part text, stripping embedded <c>&lt;system-reminder&gt;</c> blocks out of
    /// <c>conversation_history</c> and <c>tool_result_content</c> while preserving residency and server metadata.
    /// </summary>
    public static IReadOnlyList<ContextPart> ReduceContextParts(
        IReadOnlyList<ContextPart> parts,
        Func<string, long>? countTokens = null,
        InstructionBlockMarkers? instructionMarkers = null)
    {
        ArgumentNullException.ThrowIfNull(parts);
        InstructionBlockMarkers markers = instructionMarkers ?? DefaultInstructionMarkers;
        Func<string, long> counter = countTokens ?? CanonTokenCache.ApproximateO200kBase;

        List<ContextPart> reduced = new(parts.Count);
        foreach (ContextPart part in parts)
        {
            string text = part.Text ?? string.Empty;
            bool strippable = string.Equals(part.Part, "conversation_history", StringComparison.Ordinal) ||
                              string.Equals(part.Part, "tool_result_content", StringComparison.Ordinal);

            if (strippable &&
                text.Contains(markers.Open, StringComparison.Ordinal) &&
                text.Contains(markers.Close, StringComparison.Ordinal))
            {
                (List<string> blocks, string remainder) = StripInstructionBlocks(text, markers);
                long remainderTokens = counter(remainder);
                long instructionTokens = 0;
                foreach (string block in blocks)
                    instructionTokens += counter(block);

                reduced.Add(new ContextPart(
                    Part: part.Part,
                    Text: string.Empty,
                    Tokens: remainderTokens,
                    Server: part.Server,
                    Derived: true,
                    InstructionTokens: instructionTokens,
                    StrippedCount: blocks.Count,
                    Resident: part.Resident == false ? false : null));
                continue;
            }

            long? measured = CanonPricing.MeasuredCount(part.Tokens);
            bool hasStructure = text.Length > 0 || measured.HasValue;
            reduced.Add(new ContextPart(
                Part: part.Part,
                Text: string.Empty,
                Tokens: hasStructure ? (measured ?? counter(text)) : null,
                Server: part.Server,
                Derived: !measured.HasValue,
                Resident: part.Resident == false ? false : null));
        }

        return reduced;
    }

    /// <summary>
    /// Analyzes turn-by-turn context composition across the five canonical content buckets, headroom,
    /// pressure, residual attribution, and sharp fresh-input jumps.
    /// </summary>
    public static ContextAnalysis AnalyzeContext(
        IReadOnlyList<ContextTurn> turns,
        ContextCountOptions options)
    {
        ArgumentNullException.ThrowIfNull(turns);
        ArgumentNullException.ThrowIfNull(options);
        if (options.ContextLimit <= 0)
            throw new ArgumentOutOfRangeException(nameof(options), options.ContextLimit, "ContextLimit must be a positive token count.");

        InstructionBlockMarkers markers = options.InstructionMarkers ?? DefaultInstructionMarkers;
        Func<string, long> counter = options.CountTokens ?? CanonTokenCache.ApproximateO200kBase;

        AvailabilityResult availability = CanonPricing.ContextCompositionAvailability(options.Measurability);
        bool declaredNotMeasurable = !availability.IsMeasured;
        bool hasStructure = false;
        foreach (ContextTurn turn in turns)
        {
            foreach (ContextPart part in turn.Parts)
            {
                if (!string.IsNullOrEmpty(part.Text) || part.Tokens.HasValue)
                {
                    hasStructure = true;
                    break;
                }
            }

            if (hasStructure)
                break;
        }

        if (declaredNotMeasurable || !hasStructure)
        {
            return new ContextAnalysis(
                Measurable: false,
                Reason: declaredNotMeasurable ? "declared_not_measurable" : "no_message_structure",
                ContextLimit: options.ContextLimit,
                ContextLimitSource: options.ContextLimitSource,
                TurnCount: turns.Count,
                Turns: [],
                ObservedParts: [],
                ResidualTotal: 0,
                DerivedCounts: false,
                FreshJumpFactor: options.FreshJumpFactor,
                FlaggedTurns: [],
                SessionAccumulationRate: 0);
        }

        List<TurnPressure> perTurn = new(turns.Count);
        List<int> flaggedTurns = [];
        long residualTotal = 0;
        bool derivedCounts = false;
        HashSet<string> observed = new(StringComparer.Ordinal);

        for (int position = 0; position < turns.Count; position++)
        {
            ContextTurn turn = turns[position];
            Dictionary<string, long> buckets = new(StringComparer.Ordinal);
            foreach (string key in CanonValidation.CanonicalContentKeys)
                buckets[key] = 0;

            Dictionary<string, long> toolDefsByServer = new(StringComparer.Ordinal);
            long builtinToolDefTokens = 0;
            int strippedCount = 0;
            long strippedTokens = 0;
            bool derivedThisTurn = false;
            int nonResidentCount = 0;

            foreach (ContextPart part in turn.Parts)
            {
                if (part.Resident == false)
                {
                    nonResidentCount++;
                    continue;
                }

                string text = part.Text ?? string.Empty;
                long? measured = CanonPricing.MeasuredCount(part.Tokens);
                if (text.Length > 0 || measured.HasValue)
                    observed.Add(part.Part);

                bool strippable = string.Equals(part.Part, "conversation_history", StringComparison.Ordinal) ||
                                  string.Equals(part.Part, "tool_result_content", StringComparison.Ordinal);

                long partTokens;
                if (strippable &&
                    text.Contains(markers.Open, StringComparison.Ordinal) &&
                    text.Contains(markers.Close, StringComparison.Ordinal))
                {
                    (List<string> blocks, string remainder) = StripInstructionBlocks(text, markers);
                    long remainderTokens = counter(remainder);
                    buckets[part.Part] = buckets.GetValueOrDefault(part.Part) + remainderTokens;
                    derivedThisTurn = true;
                    partTokens = remainderTokens;
                    foreach (string block in blocks)
                    {
                        long blockTokens = counter(block);
                        buckets["instruction_context"] = buckets.GetValueOrDefault("instruction_context") + blockTokens;
                        strippedCount++;
                        strippedTokens += blockTokens;
                        partTokens += blockTokens;
                    }
                }
                else
                {
                    partTokens = measured ?? counter(text);
                    if (!measured.HasValue || part.Derived)
                        derivedThisTurn = true;

                    buckets[part.Part] = buckets.GetValueOrDefault(part.Part) + partTokens;
                    if (part.InstructionTokens.HasValue)
                    {
                        buckets["instruction_context"] = buckets.GetValueOrDefault("instruction_context") + part.InstructionTokens.Value;
                        strippedCount += part.StrippedCount ?? 0;
                        strippedTokens += part.InstructionTokens.Value;
                    }
                }

                if (string.Equals(part.Part, "tool_definitions", StringComparison.Ordinal))
                {
                    if (part.Server is null)
                        builtinToolDefTokens += partTokens;
                    else
                        toolDefsByServer[part.Server] = toolDefsByServer.GetValueOrDefault(part.Server) + partTokens;
                }
            }

            long bucketedTokens = 0;
            foreach (string key in CanonValidation.CanonicalContentKeys)
                bucketedTokens += buckets[key];

            long residual = turn.InputTokens - bucketedTokens;
            string attribution = derivedThisTurn ? ResidualTokenizerDrift : ResidualUnattributed;
            if (derivedThisTurn)
                derivedCounts = true;
            residualTotal += residual;

            ContextTurn? previous = position > 0 ? turns[position - 1] : null;
            long accumulationRate = previous is null ? turn.InputTokens : turn.InputTokens - previous.InputTokens;

            // The baseline is the nearest earlier turn whose fresh input was measured: an unmeasured
            // turn is not a zero, so no jump is ever flagged against it.
            FreshInputJump? freshInputJump = null;
            long? previousFresh = null;
            for (int back = position - 1; back >= 0 && !previousFresh.HasValue; back--)
                previousFresh = turns[back].FreshInput;

            if (turn.FreshInput is > 0 and long currentFresh && previousFresh.HasValue)
            {
                bool sharpRise = (previousFresh.Value > 0 && currentFresh >= options.FreshJumpFactor * previousFresh.Value) ||
                                 (previousFresh.Value == 0 && currentFresh >= options.ZeroPreviousFloor);
                if (sharpRise)
                {
                    freshInputJump = new FreshInputJump(
                        Previous: previousFresh.Value,
                        Factor: previousFresh.Value > 0 ? (double)currentFresh / previousFresh.Value : double.PositiveInfinity);
                }
            }

            perTurn.Add(new TurnPressure(
                Index: position + 1,
                InputTokens: turn.InputTokens,
                Buckets: buckets,
                ToolDefinitionsByServer: toolDefsByServer,
                BuiltinToolDefinitionTokens: builtinToolDefTokens,
                StrippedInstructionBlocks: new StrippedInstructionSummary(strippedCount, strippedTokens),
                BucketedTokens: bucketedTokens,
                Residual: new TurnResidual(residual, attribution),
                Headroom: options.ContextLimit - turn.InputTokens,
                Pressure: (double)turn.InputTokens / options.ContextLimit,
                AccumulationRate: accumulationRate,
                FreshInput: turn.FreshInput,
                FreshInputJump: freshInputJump,
                NonResidentToolDefinitions: nonResidentCount > 0 ? nonResidentCount : null));

            if (freshInputJump is not null)
                flaggedTurns.Add(position + 1);
        }

        List<string> observedParts = [];
        foreach (string key in CanonValidation.CanonicalContentKeys)
        {
            if (observed.Contains(key))
                observedParts.Add(key);
        }

        double sessionAccumulationRate = turns.Count > 1
            ? (double)(turns[^1].InputTokens - turns[0].InputTokens) / (turns.Count - 1)
            : turns[0].InputTokens;

        return new ContextAnalysis(
            Measurable: true,
            Reason: null,
            ContextLimit: options.ContextLimit,
            ContextLimitSource: options.ContextLimitSource,
            TurnCount: perTurn.Count,
            Turns: perTurn,
            ObservedParts: observedParts,
            ResidualTotal: residualTotal,
            DerivedCounts: derivedCounts,
            FreshJumpFactor: options.FreshJumpFactor,
            FlaggedTurns: flaggedTurns,
            SessionAccumulationRate: sessionAccumulationRate);
    }

    /// <summary>
    /// Ranks tool definitions by resident token rent (<c>tokens * turnsResident</c>), groups by
    /// ground-truth MCP <c>server</c>, and bounds never-invoked schema cost between cache-read and fresh-input rates.
    /// </summary>
    public static SchemaCostAnalysis RankSchemas(
        IReadOnlyList<ToolDefinition> definitions,
        int turns,
        IReadOnlyList<string> invocations,
        SchemaCostRates? rates = null,
        IReadOnlyDictionary<string, JsonElement>? measurability = null)
    {
        ArgumentNullException.ThrowIfNull(definitions);
        ArgumentNullException.ThrowIfNull(invocations);

        AvailabilityResult availability = CanonPricing.SchemaRankingAvailability(measurability);
        bool declaredNotMeasurable = !availability.IsMeasured;
        if (declaredNotMeasurable || (definitions.Count == 0 && invocations.Count > 0))
        {
            return new SchemaCostAnalysis(
                Measurable: false,
                InvocationCount: invocations.Count,
                Reason: declaredNotMeasurable ? "declared_not_measurable" : null,
                Ranked: [],
                NeverInvoked: [],
                ByServer: new Dictionary<string, long>(StringComparer.Ordinal),
                UnusedRange: null,
                Turns: turns);
        }

        SchemaCostRates effectiveRates = rates ?? DefaultTokenResidencyRates;
        HashSet<string> invoked = new(invocations, StringComparer.Ordinal);
        List<RankedTool> ranked = new(definitions.Count);
        foreach (ToolDefinition def in definitions)
        {
            int residentTurns = def.TurnsResident ?? turns;
            ranked.Add(new RankedTool(
                Name: def.Name,
                Server: def.Server,
                Cost: def.Tokens * residentTurns,
                Invoked: invoked.Contains(def.Name)));
        }

        ranked.Sort(static (a, b) =>
        {
            int cmp = b.Cost.CompareTo(a.Cost);
            return cmp != 0 ? cmp : string.CompareOrdinal(a.Name, b.Name);
        });

        List<RankedTool> neverInvoked = [];
        Dictionary<string, long> totals = new(StringComparer.Ordinal);
        foreach (RankedTool tool in ranked)
        {
            if (!tool.Invoked)
                neverInvoked.Add(tool);

            if (tool.Server is not null)
                totals[tool.Server] = totals.GetValueOrDefault(tool.Server) + tool.Cost;
        }

        Dictionary<string, long> byServer = new(StringComparer.Ordinal);
        foreach (KeyValuePair<string, long> pair in totals.OrderByDescending(static kv => kv.Value).ThenBy(static kv => kv.Key, StringComparer.Ordinal))
            byServer[pair.Key] = pair.Value;

        long tokenResidencies = 0;
        foreach (RankedTool tool in neverInvoked)
            tokenResidencies += tool.Cost;

        double cacheBound = (tokenResidencies * effectiveRates.CacheReadRate) / CanonPricing.TokensPerMillion;
        double freshBound = (tokenResidencies * effectiveRates.FreshInputRate) / CanonPricing.TokensPerMillion;

        return new SchemaCostAnalysis(
            Measurable: true,
            InvocationCount: invocations.Count,
            Reason: null,
            Ranked: ranked,
            NeverInvoked: neverInvoked,
            ByServer: byServer,
            UnusedRange: new UnusedSchemaRange(
                TokenResidencies: tokenResidencies,
                Floor: Math.Min(cacheBound, freshBound),
                Ceiling: Math.Max(cacheBound, freshBound),
                Currency: effectiveRates.Currency),
            Turns: turns);
    }

    /// <summary>
    /// Builds the hierarchical execution timeline tree for a session's spans, partitioning auxiliary work
    /// under <c>(auxiliary)</c> and grouping unreachable/cyclic primary spans under <c>(orphan:...)</c>.
    /// </summary>
    public static TimelineNode BuildTimeline(IReadOnlyList<CanonicalRecord> spans)
    {
        ArgumentNullException.ThrowIfNull(spans);

        long sessionStart = long.MaxValue;
        long[] timestamps = new long[spans.Count];
        bool[] hasTimestamp = new bool[spans.Count];
        for (int i = 0; i < spans.Count; i++)
        {
            if (TryParseTimestampMs(spans[i].Timestamp, out long ms))
            {
                timestamps[i] = ms;
                hasTimestamp[i] = true;
                if (ms < sessionStart)
                    sessionStart = ms;
            }
        }

        if (sessionStart == long.MaxValue)
            sessionStart = 0;

        List<TimelineEntry> entries = new(spans.Count);
        for (int i = 0; i < spans.Count; i++)
        {
            CanonicalRecord record = spans[i];
            IReadOnlyDictionary<string, JsonElement> attributes = record.Raw
                ?? (IReadOnlyDictionary<string, JsonElement>)new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            string terms = EvidenceTerms(record, attributes);
            bool auxiliary = ContainsAnyMarker(terms, AuxiliaryMarkers);
            bool isSubagent = ContainsAnyMarker(terms, SubagentMarkers);

            TimelineNode node = new()
            {
                SpanId = record.SpanId,
                ParentId = record.ParentSpanId,
                StartMs = hasTimestamp[i] ? Math.Max(0, timestamps[i] - sessionStart) : 0,
                DurationMs = record.DurationMs,
                Kind = record.Kind,
                Name = record.Name,
                Status = string.IsNullOrEmpty(record.Status) ? null : record.Status,
                Attributes = attributes,
                IsSubagent = isSubagent,
                IsAuxiliary = false,
                Cost = record.Cost,
            };

            entries.Add(new TimelineEntry(record, node, auxiliary));
        }

        List<TimelineEntry> primary = [];
        List<TimelineEntry> auxiliaryEntries = [];
        foreach (TimelineEntry entry in entries)
        {
            if (entry.Auxiliary)
                auxiliaryEntries.Add(entry);
            else
                primary.Add(entry);
        }

        Dictionary<string, TimelineEntry> primaryById = new(StringComparer.Ordinal);
        foreach (TimelineEntry entry in primary)
            primaryById[entry.Record.SpanId] = entry;

        Dictionary<string, TimelineNode> parentOf = new(StringComparer.Ordinal);
        foreach (TimelineEntry entry in primary)
        {
            if (entry.Record.ParentSpanId is string parentId &&
                primaryById.TryGetValue(parentId, out TimelineEntry? parent) &&
                !string.Equals(parent.Record.SpanId, entry.Record.SpanId, StringComparison.Ordinal))
            {
                parent.Node.Children.Add(entry.Node);
                parentOf[entry.Record.SpanId] = parent.Node;
            }
        }

        HashSet<string> reachable = new(StringComparer.Ordinal);
        void Visit(TimelineNode node)
        {
            if (!reachable.Add(node.SpanId))
                return;
            foreach (TimelineNode child in node.Children)
                Visit(child);
        }

        foreach (TimelineEntry entry in primary)
        {
            if (entry.Record.ParentSpanId is null)
                Visit(entry.Node);
        }

        List<TimelineEntry> orphaned = [];
        foreach (TimelineEntry entry in primary)
        {
            if (!reachable.Contains(entry.Record.SpanId))
            {
                orphaned.Add(entry);
                if (parentOf.TryGetValue(entry.Record.SpanId, out TimelineNode? cycleParent))
                {
                    for (int i = cycleParent.Children.Count - 1; i >= 0; i--)
                    {
                        if (string.Equals(cycleParent.Children[i].SpanId, entry.Record.SpanId, StringComparison.Ordinal))
                            cycleParent.Children.RemoveAt(i);
                    }
                }
            }
        }

        foreach (TimelineEntry entry in primary)
        {
            if (reachable.Contains(entry.Record.SpanId))
                SortChildren(entry.Node);
        }

        Dictionary<string, TimelineNode> orphanGroups = new(StringComparer.Ordinal);
        Dictionary<string, List<CostBlock>> orphanGroupCosts = new(StringComparer.Ordinal);
        foreach (TimelineEntry entry in orphaned)
        {
            string key = $"{entry.Record.Harness}:{entry.Record.Name}";
            if (!orphanGroups.TryGetValue(key, out TimelineNode? group))
            {
                group = new TimelineNode
                {
                    SpanId = $"(orphan:{key})",
                    ParentId = null,
                    Kind = SyntheticKind,
                    Name = entry.Record.Name,
                    Attributes = new Dictionary<string, JsonElement>(StringComparer.Ordinal),
                    IsSubagent = false,
                    IsAuxiliary = false,
                    Cost = UnpricedCost,
                };
                orphanGroups[key] = group;
                orphanGroupCosts[key] = [];
            }

            group.Children.Add(entry.Node);
            orphanGroupCosts[key].Add(entry.Record.Cost);
        }

        foreach (KeyValuePair<string, TimelineNode> pair in orphanGroups)
        {
            SortChildren(pair.Value);
            FinalizeSynthetic(pair.Value, orphanGroupCosts[pair.Key]);
        }

        Dictionary<string, TimelineEntry> auxiliaryById = new(StringComparer.Ordinal);
        foreach (TimelineEntry entry in auxiliaryEntries)
            auxiliaryById[entry.Record.SpanId] = entry;

        TimelineNode auxiliaryGroup = new()
        {
            SpanId = AuxiliaryGroupId,
            ParentId = null,
            Kind = SyntheticKind,
            Name = "auxiliary activity",
            Attributes = new Dictionary<string, JsonElement>(StringComparer.Ordinal),
            IsSubagent = false,
            IsAuxiliary = false,
            Cost = UnpricedCost,
        };

        foreach (TimelineEntry entry in auxiliaryEntries)
        {
            entry.Node.IsAuxiliary = true;
            if (entry.Record.ParentSpanId is string parentId &&
                auxiliaryById.TryGetValue(parentId, out TimelineEntry? parent) &&
                !string.Equals(parent.Record.SpanId, entry.Record.SpanId, StringComparison.Ordinal))
            {
                parent.Node.Children.Add(entry.Node);
            }
            else
            {
                auxiliaryGroup.Children.Add(entry.Node);
            }
        }

        if (auxiliaryGroup.Children.Count > 0)
        {
            SortChildrenRecursive(auxiliaryGroup);
            List<CostBlock> auxCosts = new(auxiliaryEntries.Count);
            foreach (TimelineEntry entry in auxiliaryEntries)
                auxCosts.Add(entry.Record.Cost);
            FinalizeSynthetic(auxiliaryGroup, auxCosts);
        }

        TimelineNode root = new()
        {
            SpanId = SessionRootId,
            ParentId = null,
            Kind = SyntheticKind,
            Name = "session",
            Attributes = new Dictionary<string, JsonElement>(StringComparer.Ordinal),
            IsSubagent = false,
            IsAuxiliary = false,
            Cost = UnpricedCost,
        };

        foreach (TimelineEntry entry in primary)
        {
            if (entry.Record.ParentSpanId is null)
                root.Children.Add(entry.Node);
        }

        foreach (TimelineNode orphanGroup in orphanGroups.Values)
            root.Children.Add(orphanGroup);
        if (auxiliaryGroup.Children.Count > 0)
            root.Children.Add(auxiliaryGroup);

        SortChildren(root);
        List<CostBlock> allCosts = new(entries.Count);
        foreach (TimelineEntry entry in entries)
            allCosts.Add(entry.Record.Cost);
        FinalizeSynthetic(root, allCosts);

        return root;
    }

    /// <summary>Extracts every identified subagent invocation node and its spawning parent ID.</summary>
    public static IReadOnlyList<SubagentSession> SubagentSessions(TimelineNode root)
    {
        ArgumentNullException.ThrowIfNull(root);
        List<SubagentSession> sessions = [];
        void Walk(TimelineNode node)
        {
            if (node.IsSubagent)
                sessions.Add(new SubagentSession(node.SpanId, node.ParentId, node.Name));
            foreach (TimelineNode child in node.Children)
                Walk(child);
        }

        Walk(root);
        return sessions;
    }

    /// <summary>Totals cost across every auxiliary node in <paramref name="root"/> using <see cref="CanonPricing.SumCosts"/>.</summary>
    public static CostBlock AuxiliarySpend(TimelineNode root)
    {
        ArgumentNullException.ThrowIfNull(root);
        List<CostBlock> costs = [];
        void Walk(TimelineNode node)
        {
            if (node.IsAuxiliary)
                costs.Add(node.Cost);
            foreach (TimelineNode child in node.Children)
                Walk(child);
        }

        Walk(root);
        CostTotalResult total = CanonPricing.SumCosts(costs);
        return total.Ok && total.Total is not null ? total.Total : UnpricedCost;
    }

    private static (List<string> Blocks, string Remainder) StripInstructionBlocks(
        string text,
        InstructionBlockMarkers markers)
    {
        List<string> blocks = [];
        StringBuilder remainder = new();
        int at = 0;
        while (true)
        {
            int open = text.IndexOf(markers.Open, at, StringComparison.Ordinal);
            if (open < 0)
                break;

            int close = text.IndexOf(markers.Close, open + markers.Open.Length, StringComparison.Ordinal);
            if (close < 0)
            {
                remainder.Append(text, at, text.Length - at);
                return (blocks, remainder.ToString());
            }

            blocks.Add(text[(open + markers.Open.Length)..close]);
            remainder.Append(text, at, open - at);
            at = close + markers.Close.Length;
        }

        remainder.Append(text, at, text.Length - at);
        return (blocks, remainder.ToString());
    }

    private static string EvidenceTerms(
        CanonicalRecord record,
        IReadOnlyDictionary<string, JsonElement> attributes)
    {
        StringBuilder sb = new();
        sb.Append(record.Name).Append(' ').Append(record.Op);
        foreach (KeyValuePair<string, JsonElement> entry in attributes)
        {
            if (entry.Key.StartsWith(KyberAttributePrefix, StringComparison.Ordinal))
                continue;

            sb.Append(' ').Append(entry.Key);
            if (entry.Value.ValueKind == JsonValueKind.String)
                sb.Append(' ').Append(entry.Value.GetString());
        }

        return CanonPricing.ToAsciiLower(sb.ToString());
    }

    private static bool ContainsAnyMarker(string terms, string[] markers)
    {
        foreach (string marker in markers)
        {
            if (terms.Contains(marker, StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    private static bool TryParseTimestampMs(string? timestamp, out long ms)
    {
        if (!string.IsNullOrWhiteSpace(timestamp) &&
            DateTimeOffset.TryParse(timestamp, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTimeOffset dto))
        {
            ms = dto.ToUnixTimeMilliseconds();
            return true;
        }

        ms = 0;
        return false;
    }

    private static void SortChildren(TimelineNode node)
    {
        if (node.Children.Count <= 1)
            return;

        List<TimelineNode> sorted = [.. node.Children];
        sorted.Sort(static (a, b) =>
        {
            int cmp = a.StartMs.CompareTo(b.StartMs);
            return cmp != 0 ? cmp : string.CompareOrdinal(a.SpanId, b.SpanId);
        });
        node.Children.Clear();
        foreach (TimelineNode child in sorted)
            node.Children.Add(child);
    }

    private static void SortChildrenRecursive(TimelineNode node)
    {
        SortChildren(node);
        foreach (TimelineNode child in node.Children)
            SortChildrenRecursive(child);
    }

    private static void FinalizeSynthetic(TimelineNode node, IReadOnlyList<CostBlock> costs)
    {
        if (node.Children.Count > 0)
        {
            long start = long.MaxValue;
            long end = long.MinValue;
            foreach (TimelineNode child in node.Children)
            {
                if (child.StartMs < start)
                    start = child.StartMs;
                long childEnd = child.StartMs + child.DurationMs;
                if (childEnd > end)
                    end = childEnd;
            }

            node.StartMs = start;
            node.DurationMs = Math.Max(0, end - start);
        }

        CostTotalResult total = CanonPricing.SumCosts(costs);
        node.Cost = total.Ok && total.Total is not null ? total.Total : UnpricedCost;
    }

    private record TimelineEntry(CanonicalRecord Record, TimelineNode Node, bool Auxiliary);
}
