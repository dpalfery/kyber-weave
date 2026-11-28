using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace KyberDash.Domain;

/// <summary>
/// Counts the tokens in <paramref name="text"/> with the tokenizer of <paramref name="model"/> (decision D17).
/// </summary>
/// <remarks>
/// The analysis cannot see the tokenizer seam, which lives outside the domain, so the caller
/// supplies the count; a blank model counts under the tokenizer's labelled stand-in (D19).
/// </remarks>
/// <param name="text">Text to count.</param>
/// <param name="model">Model the text was sent to; <see langword="null"/> or blank when unknown.</param>
/// <returns>Number of tokens the model's tokenizer gives the text, labelled with that tokenizer.</returns>
public delegate TokenCount TextTokenCounter(string text, string? model);

/// <summary>A tokenizer that counted text-derived figures, and whether it is the model's own.</summary>
/// <param name="Tokenizer">Tokenizer name, such as <c>o200k_base</c>.</param>
/// <param name="IsModelOwn">Whether the tokenizer is the counted model's own rather than a stand-in.</param>
public record TokenizerLabel(string Tokenizer, bool IsModelOwn)
{
    /// <summary>Distinct labels in tokenizer-name order, so equal sets compare and serialize alike.</summary>
    /// <param name="labels">Labels to order; duplicates collapse.</param>
    /// <returns>The distinct labels ordered by tokenizer name, then model-own last.</returns>
    public static IReadOnlyList<TokenizerLabel> Ordered(IEnumerable<TokenizerLabel> labels)
    {
        ArgumentNullException.ThrowIfNull(labels);
        return labels
            .Distinct()
            .OrderBy(static label => label.Tokenizer, StringComparer.Ordinal)
            .ThenBy(static label => label.IsModelOwn)
            .ToList();
    }
}

/// <summary>One piece of a turn's context payload keyed by canonical part type.</summary>
/// <param name="Part">Canonical part type, such as <c>conversation_history</c>.</param>
/// <param name="Text">Part text; empty once the part is reduced.</param>
/// <param name="Tokens">Counted tokens, when already known.</param>
/// <param name="Server">MCP server a tool-definition part belongs to.</param>
/// <param name="Derived">Whether <paramref name="Tokens"/> was counted locally rather than reported.</param>
/// <param name="InstructionTokens">Tokens of instruction blocks stripped out of the part.</param>
/// <param name="StrippedCount">Number of instruction blocks stripped out of the part.</param>
/// <param name="Resident">Explicit <see langword="false"/> for deferred tools not loaded into resident context.</param>
/// <param name="Model">Model the part was sent to, whose tokenizer counts its text; <see langword="null"/> when unknown.</param>
/// <param name="Tokenizer">Tokenizer that counted <paramref name="Tokens"/> from text; <see langword="null"/> for a reported or absent count.</param>
/// <param name="TokenizerIsModelOwn">Whether <paramref name="Tokenizer"/> is the model's own; <see langword="null"/> when <paramref name="Tokenizer"/> is.</param>
public record ContextPart(
    string Part,
    string Text,
    long? Tokens = null,
    string? Server = null,
    bool Derived = false,
    long? InstructionTokens = null,
    int? StrippedCount = null,
    bool? Resident = null,
    string? Model = null,
    string? Tokenizer = null,
    bool? TokenizerIsModelOwn = null)
{
    /// <summary>Adapts a persisted <see cref="ContentPart"/> into an analysis <see cref="ContextPart"/>.</summary>
    /// <param name="part">Persisted content part.</param>
    /// <param name="model">Model the part's record was sent to; <see langword="null"/> when unknown.</param>
    public static ContextPart FromContentPart(ContentPart part, string? model = null)
    {
        ArgumentNullException.ThrowIfNull(part);
        return new ContextPart(
            Part: part.Part,
            Text: part.Text,
            Tokens: part.Tokens,
            Server: part.Server,
            Resident: part.Resident,
            Model: model,
            Tokenizer: part.Tokenizer,
            TokenizerIsModelOwn: part.TokenizerIsModelOwn);
    }

    /// <summary>The label of this part's text-derived count, or <see langword="null"/> when it has none.</summary>
    public TokenizerLabel? Label => Tokenizer is null ? null : new TokenizerLabel(Tokenizer, TokenizerIsModelOwn ?? false);
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
    int? NonResidentToolDefinitions = null)
{
    /// <summary>
    /// Tokenizers that counted this turn's text-derived figures, in name order; empty when every
    /// figure is harness-reported. More than one entry means the turn's sums mix tokenizers.
    /// </summary>
    public IReadOnlyList<TokenizerLabel> Tokenizers { get; init; } = [];
}

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
    double SessionAccumulationRate)
{
    /// <summary>Union of every turn's <see cref="TurnPressure.Tokenizers"/>, in name order.</summary>
    public IReadOnlyList<TokenizerLabel> Tokenizers { get; init; } = [];
}

/// <summary>Tool definition entry supplied to <see cref="CanonContextAnalysis.RankSchemas"/>.</summary>
public record ToolDefinition(
    string Name,
    string? Server = null,
    long Tokens = 0,
    int? TurnsResident = null,
    string? Tokenizer = null,
    bool? TokenizerIsModelOwn = null);

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
    /// <param name="parts">Parts to reduce.</param>
    /// <param name="countTokens">Counts each part's text with the tokenizer of the part's own model.</param>
    /// <param name="instructionMarkers">Delimiters of embedded instruction blocks; the system-reminder pair when omitted.</param>
    public static IReadOnlyList<ContextPart> ReduceContextParts(
        IReadOnlyList<ContextPart> parts,
        TextTokenCounter countTokens,
        InstructionBlockMarkers? instructionMarkers = null)
    {
        ArgumentNullException.ThrowIfNull(parts);
        ArgumentNullException.ThrowIfNull(countTokens);
        InstructionBlockMarkers markers = instructionMarkers ?? DefaultInstructionMarkers;

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
                TokenCount remainderCount = countTokens(remainder, part.Model);
                long remainderTokens = remainderCount.Count;
                long instructionTokens = 0;
                foreach (string block in blocks)
                    instructionTokens += countTokens(block, part.Model).Count;

                reduced.Add(new ContextPart(
                    Part: part.Part,
                    Text: string.Empty,
                    Tokens: remainderTokens,
                    Server: part.Server,
                    Derived: true,
                    InstructionTokens: instructionTokens,
                    StrippedCount: blocks.Count,
                    Resident: part.Resident == false ? false : null,
                    Model: part.Model,
                    Tokenizer: remainderCount.Tokenizer,
                    TokenizerIsModelOwn: remainderCount.IsModelOwn));
                continue;
            }

            long? measured = CanonPricing.MeasuredCount(part.Tokens);
            bool hasStructure = text.Length > 0 || measured.HasValue;
            TokenCount? counted = hasStructure && !measured.HasValue ? countTokens(text, part.Model) : null;
            long? tokens = measured ?? counted?.Count;
            reduced.Add(new ContextPart(
                Part: part.Part,
                Text: string.Empty,
                Tokens: tokens,
                Server: part.Server,
                Derived: !measured.HasValue,
                Resident: part.Resident == false ? false : null,
                Model: part.Model,
                Tokenizer: counted?.Tokenizer ?? (measured.HasValue ? part.Tokenizer : null),
                TokenizerIsModelOwn: counted?.IsModelOwn ?? (measured.HasValue ? part.TokenizerIsModelOwn : null)));
        }

        return reduced;
    }

    /// <summary>
    /// Analyzes turn-by-turn context composition across the five canonical content buckets, headroom,
    /// pressure, residual attribution, and sharp fresh-input jumps.
    /// </summary>
    /// <param name="turns">Turns in session order.</param>
    /// <param name="options">Context limit, jump thresholds, markers, and measurability.</param>
    /// <param name="countTokens">Counts each uncounted part's text with the tokenizer of the part's own model.</param>
    public static ContextAnalysis AnalyzeContext(
        IReadOnlyList<ContextTurn> turns,
        ContextCountOptions options,
        TextTokenCounter countTokens)
    {
        ArgumentNullException.ThrowIfNull(turns);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(countTokens);
        if (options.ContextLimit <= 0)
            throw new ArgumentOutOfRangeException(nameof(options), options.ContextLimit, "ContextLimit must be a positive token count.");

        InstructionBlockMarkers markers = options.InstructionMarkers ?? DefaultInstructionMarkers;

        AvailabilityResult availability = CanonPricing.ContextCompositionAvailability(options.Measurability);
        bool declaredNotMeasurable = !availability.IsMeasured;
        bool hasStructure = HasMessageStructure(turns);

        if (declaredNotMeasurable || !hasStructure)
            return NotMeasurableAnalysis(turns, options, declaredNotMeasurable);

        List<TurnPressure> perTurn = new(turns.Count);
        List<int> flaggedTurns = [];
        long residualTotal = 0;
        bool derivedCounts = false;
        HashSet<string> observed = new(StringComparer.Ordinal);

        for (int position = 0; position < turns.Count; position++)
        {
            ContextTurn turn = turns[position];
            TurnAccumulator accumulator = new(markers, countTokens, observed);
            foreach (ContextPart part in turn.Parts)
                accumulator.Add(part);

            Dictionary<string, long> buckets = accumulator.Buckets;
            long bucketedTokens = 0;
            foreach (string key in CanonValidation.CanonicalContentKeys)
                bucketedTokens += buckets[key];

            long residual = turn.InputTokens - bucketedTokens;
            string attribution = accumulator.DerivedThisTurn ? ResidualTokenizerDrift : ResidualUnattributed;
            if (accumulator.DerivedThisTurn)
                derivedCounts = true;
            residualTotal += residual;

            ContextTurn? previous = position > 0 ? turns[position - 1] : null;
            long accumulationRate = previous is null ? turn.InputTokens : turn.InputTokens - previous.InputTokens;
            FreshInputJump? freshInputJump = DetectFreshInputJump(turns, position, options);

            perTurn.Add(new TurnPressure(
                Index: position + 1,
                InputTokens: turn.InputTokens,
                Buckets: buckets,
                ToolDefinitionsByServer: accumulator.ToolDefinitionsByServer,
                BuiltinToolDefinitionTokens: accumulator.BuiltinToolDefinitionTokens,
                StrippedInstructionBlocks: new StrippedInstructionSummary(accumulator.StrippedCount, accumulator.StrippedTokens),
                BucketedTokens: bucketedTokens,
                Residual: new TurnResidual(residual, attribution),
                Headroom: options.ContextLimit - turn.InputTokens,
                Pressure: (double)turn.InputTokens / options.ContextLimit,
                AccumulationRate: accumulationRate,
                FreshInput: turn.FreshInput,
                FreshInputJump: freshInputJump,
                NonResidentToolDefinitions: accumulator.NonResidentCount > 0 ? accumulator.NonResidentCount : null)
            {
                Tokenizers = TokenizerLabel.Ordered(accumulator.TurnLabels),
            });

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
            SessionAccumulationRate: sessionAccumulationRate)
        {
            Tokenizers = TokenizerLabel.Ordered(perTurn.SelectMany(static turn => turn.Tokenizers)),
        };
    }

    private static bool HasMessageStructure(IReadOnlyList<ContextTurn> turns)
    {
        foreach (ContextTurn turn in turns)
        {
            foreach (ContextPart part in turn.Parts)
            {
                if (!string.IsNullOrEmpty(part.Text) || part.Tokens.HasValue)
                    return true;
            }
        }

        return false;
    }

    private static ContextAnalysis NotMeasurableAnalysis(
        IReadOnlyList<ContextTurn> turns,
        ContextCountOptions options,
        bool declaredNotMeasurable)
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

    private static FreshInputJump? DetectFreshInputJump(
        IReadOnlyList<ContextTurn> turns,
        int position,
        ContextCountOptions options)
    {
        // The baseline is the nearest earlier turn whose fresh input was measured: an unmeasured
        // turn is not a zero, so no jump is ever flagged against it.
        long? previousFresh = null;
        for (int back = position - 1; back >= 0 && !previousFresh.HasValue; back--)
            previousFresh = turns[back].FreshInput;

        ContextTurn turn = turns[position];
        if (turn.FreshInput is not (> 0 and long currentFresh) || !previousFresh.HasValue)
            return null;

        bool sharpRise = (previousFresh.Value > 0 && currentFresh >= options.FreshJumpFactor * previousFresh.Value) ||
                         (previousFresh.Value == 0 && currentFresh >= options.ZeroPreviousFloor);
        if (!sharpRise)
            return null;

        return new FreshInputJump(
            Previous: previousFresh.Value,
            Factor: previousFresh.Value > 0 ? (double)currentFresh / previousFresh.Value : double.PositiveInfinity);
    }

    private sealed class TurnAccumulator
    {
        private readonly InstructionBlockMarkers _markers;
        private readonly TextTokenCounter _countTokens;
        private readonly HashSet<string> _observed;

        public TurnAccumulator(
            InstructionBlockMarkers markers,
            TextTokenCounter countTokens,
            HashSet<string> observed)
        {
            _markers = markers;
            _countTokens = countTokens;
            _observed = observed;
            Buckets = new Dictionary<string, long>(StringComparer.Ordinal);
            foreach (string key in CanonValidation.CanonicalContentKeys)
                Buckets[key] = 0;
        }

        public Dictionary<string, long> Buckets { get; }

        public Dictionary<string, long> ToolDefinitionsByServer { get; } = new(StringComparer.Ordinal);

        public long BuiltinToolDefinitionTokens { get; private set; }

        public int StrippedCount { get; private set; }

        public long StrippedTokens { get; private set; }

        public bool DerivedThisTurn { get; private set; }

        public int NonResidentCount { get; private set; }

        public HashSet<TokenizerLabel> TurnLabels { get; } = [];

        public void Add(ContextPart part)
        {
            if (part.Resident == false)
            {
                NonResidentCount++;
                return;
            }

            string text = part.Text ?? string.Empty;
            long? measured = CanonPricing.MeasuredCount(part.Tokens);
            if (text.Length > 0 || measured.HasValue)
                _observed.Add(part.Part);

            bool strippable = string.Equals(part.Part, "conversation_history", StringComparison.Ordinal) ||
                              string.Equals(part.Part, "tool_result_content", StringComparison.Ordinal);

            long partTokens;
            if (strippable &&
                text.Contains(_markers.Open, StringComparison.Ordinal) &&
                text.Contains(_markers.Close, StringComparison.Ordinal))
            {
                (List<string> blocks, string remainder) = StripInstructionBlocks(text, _markers);
                TokenCount remainderCount = _countTokens(remainder, part.Model);
                TurnLabels.Add(new TokenizerLabel(remainderCount.Tokenizer, remainderCount.IsModelOwn));
                long remainderTokens = remainderCount.Count;
                Buckets[part.Part] = Buckets.GetValueOrDefault(part.Part) + remainderTokens;
                DerivedThisTurn = true;
                partTokens = remainderTokens;
                foreach (string block in blocks)
                {
                    TokenCount blockCount = _countTokens(block, part.Model);
                    TurnLabels.Add(new TokenizerLabel(blockCount.Tokenizer, blockCount.IsModelOwn));
                    long blockTokens = blockCount.Count;
                    Buckets["instruction_context"] = Buckets.GetValueOrDefault("instruction_context") + blockTokens;
                    StrippedCount++;
                    StrippedTokens += blockTokens;
                    partTokens += blockTokens;
                }
            }
            else
            {
                if (measured.HasValue)
                {
                    partTokens = measured.Value;
                    if (part.Label is TokenizerLabel measuredLabel)
                        TurnLabels.Add(measuredLabel);
                }
                else
                {
                    TokenCount counted = _countTokens(text, part.Model);
                    TurnLabels.Add(new TokenizerLabel(counted.Tokenizer, counted.IsModelOwn));
                    partTokens = counted.Count;
                }

                if (!measured.HasValue || part.Derived)
                    DerivedThisTurn = true;

                Buckets[part.Part] = Buckets.GetValueOrDefault(part.Part) + partTokens;
                if (part.InstructionTokens.HasValue)
                {
                    Buckets["instruction_context"] = Buckets.GetValueOrDefault("instruction_context") + part.InstructionTokens.Value;
                    StrippedCount += part.StrippedCount ?? 0;
                    StrippedTokens += part.InstructionTokens.Value;
                }
            }

            if (string.Equals(part.Part, "tool_definitions", StringComparison.Ordinal))
            {
                if (part.Server is null)
                    BuiltinToolDefinitionTokens += partTokens;
                else
                    ToolDefinitionsByServer[part.Server] = ToolDefinitionsByServer.GetValueOrDefault(part.Server) + partTokens;
            }
        }
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
        if (declaredNotMeasurable || definitions.Count == 0)
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

        long[] timestamps = new long[spans.Count];
        bool[] hasTimestamp = new bool[spans.Count];
        long sessionStart = ResolveSessionStart(spans, timestamps, hasTimestamp);

        List<TimelineEntry> entries = BuildEntries(spans, timestamps, hasTimestamp, sessionStart);
        List<TimelineEntry> primary = [];
        List<TimelineEntry> auxiliaryEntries = [];
        foreach (TimelineEntry entry in entries)
        {
            if (entry.Auxiliary)
                auxiliaryEntries.Add(entry);
            else
                primary.Add(entry);
        }

        HashSet<string> reachable = LinkPrimary(primary, out Dictionary<string, TimelineNode> parentOf);
        List<TimelineEntry> orphaned = DetachOrphans(primary, parentOf, reachable);

        foreach (TimelineEntry entry in primary)
        {
            if (reachable.Contains(entry.Record.SpanId))
                SortChildren(entry.Node);
        }

        Dictionary<string, TimelineNode> orphanGroups = BuildOrphanGroups(orphaned);
        TimelineNode auxiliaryGroup = BuildAuxiliaryGroup(auxiliaryEntries);

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

    private static long ResolveSessionStart(
        IReadOnlyList<CanonicalRecord> spans,
        long[] timestamps,
        bool[] hasTimestamp)
    {
        long sessionStart = long.MaxValue;
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

        return sessionStart == long.MaxValue ? 0 : sessionStart;
    }

    private static List<TimelineEntry> BuildEntries(
        IReadOnlyList<CanonicalRecord> spans,
        long[] timestamps,
        bool[] hasTimestamp,
        long sessionStart)
    {
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

        return entries;
    }

    private static HashSet<string> LinkPrimary(
        List<TimelineEntry> primary,
        out Dictionary<string, TimelineNode> parentOf)
    {
        Dictionary<string, TimelineEntry> primaryById = new(StringComparer.Ordinal);
        foreach (TimelineEntry entry in primary)
            primaryById[entry.Record.SpanId] = entry;

        parentOf = new(StringComparer.Ordinal);
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
        foreach (TimelineEntry entry in primary)
        {
            if (entry.Record.ParentSpanId is null)
                VisitNode(entry.Node, reachable);
        }

        return reachable;
    }

    private static void VisitNode(TimelineNode node, HashSet<string> reachable)
    {
        if (!reachable.Add(node.SpanId))
            return;
        foreach (TimelineNode child in node.Children)
            VisitNode(child, reachable);
    }

    private static List<TimelineEntry> DetachOrphans(
        List<TimelineEntry> primary,
        Dictionary<string, TimelineNode> parentOf,
        HashSet<string> reachable)
    {
        List<TimelineEntry> orphaned = [];
        foreach (TimelineEntry entry in primary)
        {
            if (reachable.Contains(entry.Record.SpanId))
                continue;

            orphaned.Add(entry);
            if (parentOf.TryGetValue(entry.Record.SpanId, out TimelineNode? cycleParent))
                RemoveChild(cycleParent, entry.Record.SpanId);
        }

        return orphaned;
    }

    private static void RemoveChild(TimelineNode parent, string spanId)
    {
        for (int i = parent.Children.Count - 1; i >= 0; i--)
        {
            if (string.Equals(parent.Children[i].SpanId, spanId, StringComparison.Ordinal))
                parent.Children.RemoveAt(i);
        }
    }

    private static Dictionary<string, TimelineNode> BuildOrphanGroups(List<TimelineEntry> orphaned)
    {
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

        return orphanGroups;
    }

    private static TimelineNode BuildAuxiliaryGroup(List<TimelineEntry> auxiliaryEntries)
    {
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

        return auxiliaryGroup;
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
