using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using KyberDash.Data.Collector;
using KyberDash.Data.Collector.Sources;
using KyberDash.Domain;

namespace KyberDash.Data.Harnesses.Devin;

/// <summary>
/// Typed representation of one tool invocation on a Devin transcript step
/// (<c>tool_calls[]</c> entries carrying <c>tool_call_id</c>, <c>function_name</c>,
/// and raw <c>arguments</c> JSON).
/// </summary>
/// <param name="CallId">Upstream tool call identifier (<c>tool_call_id</c>), when present.</param>
/// <param name="FunctionName">Raw tool function name (<c>function_name</c>).</param>
/// <param name="ArgumentsJson">Raw <c>arguments</c> JSON payload text, or <see langword="null"/> when absent or JSON null.</param>
public record DevinToolCall(
    string? CallId,
    string FunctionName,
    string? ArgumentsJson);

/// <summary>
/// Token usage counters on a Devin step (<c>metrics</c>: <c>prompt_tokens</c>,
/// <c>completion_tokens</c>, <c>cached_tokens</c>, and <c>extra.cache_creation_input_tokens</c>).
/// </summary>
/// <param name="PromptTokens">Exclusive input tokens (<c>prompt_tokens</c>), when present.</param>
/// <param name="CompletionTokens">Generated output tokens (<c>completion_tokens</c>), when present.</param>
/// <param name="CachedTokens">Cache-read tokens (<c>cached_tokens</c>), when present.</param>
/// <param name="CacheCreationInputTokens">Cache-creation tokens (<c>extra.cache_creation_input_tokens</c>), when present.</param>
public record DevinStepMetrics(
    double? PromptTokens,
    double? CompletionTokens,
    double? CachedTokens,
    double? CacheCreationInputTokens);

/// <summary>
/// Native metadata object on a Devin transcript step (<c>metadata</c>), flattening the legacy
/// <c>metadata.metrics</c> location (<c>input_tokens</c>, <c>output_tokens</c>,
/// <c>cache_creation_tokens</c>, <c>cache_read_tokens</c>) alongside the step-level fields.
/// </summary>
/// <param name="CreatedAt">ISO-8601 UTC step timestamp (<c>created_at</c>), when present.</param>
/// <param name="CommittedAcuCost">Committed ACU cost (<c>committed_acu_cost</c>), when present.</param>
/// <param name="GenerationModel">Backend generation model (<c>generation_model</c>), when present.</param>
/// <param name="IsUserInput">Whether this step is a user turn (<c>is_user_input</c>).</param>
/// <param name="InputTokens">Legacy input tokens (<c>metrics.input_tokens</c>), when present.</param>
/// <param name="OutputTokens">Legacy output tokens (<c>metrics.output_tokens</c>), when present.</param>
/// <param name="CacheCreationTokens">Legacy cache-creation tokens (<c>metrics.cache_creation_tokens</c>), when present.</param>
/// <param name="CacheReadTokens">Legacy cache-read tokens (<c>metrics.cache_read_tokens</c>), when present.</param>
/// <param name="TotalTimeMs">Reported call duration (<c>metadata.metrics.total_time_ms</c> on a <c>message_nodes</c> assistant node), when present; never estimated.</param>
public record DevinStepMetadata(
    string? CreatedAt,
    double? CommittedAcuCost,
    string? GenerationModel,
    bool IsUserInput,
    double? InputTokens,
    double? OutputTokens,
    double? CacheCreationTokens,
    double? CacheReadTokens,
    double? TotalTimeMs = null);

/// <summary>
/// Native <c>extra</c> object on a Devin transcript step, carrying the ATIF v1.7
/// <c>committed_acu_cost</c> and <c>generation_model</c> overrides.
/// </summary>
/// <param name="CommittedAcuCost">Committed ACU cost (<c>extra.committed_acu_cost</c>), when present.</param>
/// <param name="GenerationModel">Backend generation model (<c>extra.generation_model</c>), when present.</param>
public record DevinStepExtra(
    double? CommittedAcuCost,
    string? GenerationModel);

/// <summary>
/// Typed representation of one step in a Devin CLI agent-trajectory transcript.
/// </summary>
/// <param name="StepId">Native step identifier (<c>step_id</c>), coerced to text.</param>
/// <param name="Source">Step source (<c>source</c>), when present.</param>
/// <param name="ModelName">Step-level model name (<c>model_name</c>), when present.</param>
/// <param name="MessageText">Normalized step message text (text parts trimmed and joined with image paths), or <see langword="null"/> when absent.</param>
/// <param name="ToolCalls">Tool invocations issued on this step (<c>tool_calls</c>).</param>
/// <param name="Extra">Native <c>extra</c> overrides, when present.</param>
/// <param name="Metrics">Step-level token metrics (<c>metrics</c>), when present.</param>
/// <param name="Metadata">Native step metadata (<c>metadata</c>), when present.</param>
public record DevinStep(
    string StepId,
    string? Source,
    string? ModelName,
    string? MessageText,
    IReadOnlyList<DevinToolCall> ToolCalls,
    DevinStepExtra? Extra,
    DevinStepMetrics? Metrics,
    DevinStepMetadata? Metadata);

/// <summary>
/// Non-fatal parse problem encountered while reading a Devin transcript.
/// </summary>
/// <param name="LineNumber">1-based step index within the transcript (0 for document-level failures).</param>
/// <param name="Code">Diagnostic problem code.</param>
/// <param name="Message">Sanitized diagnostic description.</param>
public record DevinParseProblem(
    int LineNumber,
    string Code,
    string Message);

/// <summary>
/// Parsed Devin CLI agent-trajectory transcript produced by <see cref="DevinMapper.Parse"/>.
/// </summary>
/// <param name="IsValid">Whether the document root parsed as a transcript object (malformed roots yield problems instead of records).</param>
/// <param name="SessionId">Native session identifier (<c>session_id</c>), or <see langword="null"/> when absent.</param>
/// <param name="AgentModelName">Transcript agent model name (<c>agent.model_name</c>), or <see langword="null"/> when absent.</param>
/// <param name="Steps">Transcript steps in order.</param>
/// <param name="ParseProblems">Non-fatal parse diagnostics.</param>
/// <param name="Title">Session title (<c>title</c> on a <c>message_nodes</c> export), or <see langword="null"/> when absent.</param>
/// <param name="WorkingDirectory">Session working directory (<c>working_directory</c> on a <c>message_nodes</c> export), or <see langword="null"/> when absent.</param>
/// <param name="FromMessageNodes">Whether the document came from a <c>message_nodes</c> export, where an assistant step without metrics still maps to a token-less record.</param>
public record DevinTranscriptDocument(
    bool IsValid,
    string? SessionId,
    string? AgentModelName,
    IReadOnlyList<DevinStep> Steps,
    IReadOnlyList<DevinParseProblem> ParseProblems,
    string? Title = null,
    string? WorkingDirectory = null,
    bool FromMessageNodes = false);

/// <summary>
/// Offline short display-name resolution porting the pricing-state-free core of
/// <c>getShortModelName</c> in <c>dash/src/pricing/models.ts</c>: the
/// <c>deriveClaudeShortName</c> family derivation plus the static
/// <c>SHORT_NAMES</c> table (longest-prefix wins). User aliases, provider-namespace
/// stripping, and catalog-backed lookups need pricing state and resolve at read time.
/// </summary>
internal static partial class DevinModelShortNames
{
    private static readonly IReadOnlyDictionary<string, string> ShortNames =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["claude-fable-5"] = "Fable 5",
            ["claude-mythos-5"] = "Mythos 5",
            ["claude-3-7-sonnet"] = "Sonnet 3.7",
            ["claude-3-5-sonnet"] = "Sonnet 3.5",
            ["claude-3-5-haiku"] = "Haiku 3.5",
            ["gpt-4o-mini"] = "GPT-4o Mini",
            ["gpt-4o"] = "GPT-4o",
            ["gpt-4.1-nano"] = "GPT-4.1 Nano",
            ["gpt-4.1-mini"] = "GPT-4.1 Mini",
            ["gpt-4.1"] = "GPT-4.1",
            ["codex-auto-review"] = "Codex Auto Review",
            ["gpt-5.5-pro"] = "GPT-5.5 Pro",
            ["gpt-5.5"] = "GPT-5.5",
            ["gpt-5.4-pro"] = "GPT-5.4 Pro",
            ["gpt-5.4-nano"] = "GPT-5.4 Nano",
            ["gpt-5.4-mini"] = "GPT-5.4 Mini",
            ["gpt-5.4"] = "GPT-5.4",
            ["gpt-5.3-codex-spark"] = "GPT-5.3 Codex Spark",
            ["gpt-5.3-codex"] = "GPT-5.3 Codex",
            ["gpt-5.3"] = "GPT-5.3",
            ["gpt-5.2-pro"] = "GPT-5.2 Pro",
            ["gpt-5.2-low"] = "GPT-5.2 Low",
            ["gpt-5.2"] = "GPT-5.2",
            ["gpt-5.1-codex-mini"] = "GPT-5.1 Codex Mini",
            ["gpt-5.1-codex"] = "GPT-5.1 Codex",
            ["gpt-5.1"] = "GPT-5.1",
            ["gpt-5-pro"] = "GPT-5 Pro",
            ["gpt-5-nano"] = "GPT-5 Nano",
            ["gpt-5-mini"] = "GPT-5 Mini",
            ["gpt-5"] = "GPT-5",
            ["gemini-3.5-flash"] = "Gemini 3.5 Flash",
            ["gemini-3.1-pro-preview"] = "Gemini 3.1 Pro",
            ["gemini-3-flash-preview"] = "Gemini 3 Flash",
            ["gemini-2.5-pro"] = "Gemini 2.5 Pro",
            ["gemini-2.5-flash"] = "Gemini 2.5 Flash",
            ["kimi-k2-thinking-turbo"] = "Kimi K2 Thinking Turbo",
            ["kimi-k2-thinking"] = "Kimi K2 Thinking",
            ["kimi-k3"] = "Kimi K3",
            ["kimi-k2p6"] = "Kimi K2.6",
            ["kimi-thinking-preview"] = "Kimi Thinking",
            ["kimi-k2.6"] = "Kimi K2.6",
            ["kimi-k2.5"] = "Kimi K2.5",
            ["kimi-k2p5"] = "Kimi K2.5",
            ["kimi-k2-instruct"] = "Kimi K2 Instruct",
            ["kimi-k2-0905"] = "Kimi K2",
            ["kimi-k2"] = "Kimi K2",
            ["kimi-latest"] = "Kimi Latest",
            ["moonshot-v1"] = "Moonshot v1",
            ["deepseek-v4-pro"] = "DeepSeek v4 Pro",
            ["deepseek-v4-flash"] = "DeepSeek v4 Flash",
            ["deepseek-coder-max"] = "DeepSeek Coder Max",
            ["deepseek-coder"] = "DeepSeek Coder",
            ["deepseek-r1"] = "DeepSeek R1",
            ["o4-mini"] = "o4-mini",
            ["o3"] = "o3",
            ["MiniMax-M2.7-highspeed"] = "MiniMax M2.7 Highspeed",
            ["MiniMax-M2.7"] = "MiniMax M2.7",
            ["glm-5p1"] = "GLM-5.2",
            ["grok-build-0.1"] = "Grok Build",
            ["grok-composer-2.5-fast"] = "Grok Composer 2.5 Fast",
            ["glm-5p2"] = "GLM-5.2",
            ["qwen3p7-plus"] = "Qwen 3.7 Plus",
            ["kimi-k2p7-code"] = "Kimi K2.7 Code",
            ["gpt-5.6-sol"] = "GPT-5.6 Sol",
            ["gpt-5.6-terra"] = "GPT-5.6 Terra",
            ["gpt-5.6-luna"] = "GPT-5.6 Luna",
            ["grok-4.5"] = "Grok 4.5",
            ["grok-4.5-build"] = "Grok 4.5 (build)",
            ["qwen3.7-max"] = "Qwen 3.7 Max",
            ["mimo-v2.5-pro"] = "MiMo v2.5 Pro",
            ["mimo-v2.5"] = "MiMo v2.5",
            ["mimo-v2-flash"] = "MiMo v2 Flash",
            ["minimax-m3"] = "MiniMax M3",
            ["MiniMax-M3"] = "MiniMax M3",
        };

    private static readonly (string Key, string Name)[] SortedShortNames =
        ShortNames.OrderByDescending(static entry => entry.Key.Length).Select(static entry => (entry.Key, entry.Value)).ToArray();

    /// <summary>
    /// Resolves a raw model id to its short display name using the static table and
    /// the <c>claude-&lt;family&gt;-&lt;major&gt;[-&lt;minor&gt;]</c> derivation, passing
    /// unknown ids through unchanged.
    /// </summary>
    /// <param name="model">Raw model identifier.</param>
    /// <returns>The short display name, or <paramref name="model"/> when unrecognized.</returns>
    internal static string ResolveShortName(string model)
    {
        string? claude = DeriveClaudeShortName(model);
        if (claude is not null)
        {
            return claude;
        }

        foreach ((string key, string name) in SortedShortNames)
        {
            if (model.Equals(key, StringComparison.Ordinal)
                || model.StartsWith(key + "-", StringComparison.Ordinal))
            {
                return name;
            }
        }

        return model;
    }

    [GeneratedRegex(@"^claude-(opus|sonnet|haiku)-(\d+)(?:-(\d+))?", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex ClaudeFamilyRegex();

    private static string? DeriveClaudeShortName(string model)
    {
        Match match = ClaudeFamilyRegex().Match(model);
        if (!match.Success)
        {
            return null;
        }

        string family = match.Groups[1].Value switch
        {
            "opus" => "Opus",
            "sonnet" => "Sonnet",
            "haiku" => "Haiku",
            _ => string.Empty,
        };
        if (family.Length == 0)
        {
            return null;
        }

        string minor = match.Groups[3].Success ? $".{match.Groups[3].Value}" : string.Empty;
        return string.Create(CultureInfo.InvariantCulture, $"{family} {match.Groups[2].Value}{minor}");
    }

}

/// <summary>
/// Pure <see cref="IHarnessMapper"/> porting <c>dash/src/providers/devin.ts</c> into normalized
/// <see cref="CollectedBatch"/> records without touching the filesystem or ambient clock.
/// </summary>
/// <remarks>
/// Only the transcript surface is ported: usage-bearing non-user steps map to <c>llm.invoke</c>
/// records with harness-reported tokens, tool calls map to child <c>tool.invoke</c> spans, and the
/// nearest preceding user message maps to the <c>user_message</c> content part. Live-only surfaces
/// (<c>sessions.db</c> project/title/model/timestamp enrichment and hidden-session filtering,
/// discovery, the once-per-process unpriced-rate stderr warning, and fiscal config reads) have no
/// offline input and are not persisted. ACU pricing needs the caller-supplied
/// <c>devin.acuUsdRate</c>; without it costs stay unreported (never <c>$0</c>) so rate-table
/// estimation applies downstream. Assistant message text and observations carry no call-level
/// fields in the deleted provider and are dropped.
/// </remarks>
public partial class DevinMapper : IHarnessMapper
{
    /// <summary>Canonical harness identifier for Devin CLI transcripts.</summary>
    public const string DevinHarnessId = "devin";

    /// <summary>Diagnostic code emitted when a Devin transcript cannot be parsed.</summary>
    public const string MalformedDocumentCode = "DEVIN_MALFORMED_DOCUMENT";

    private const string DefaultModelName = "devin";

    private static readonly HashSet<string> EffortTiers = new(StringComparer.Ordinal)
    {
        "xhigh", "high", "medium", "low",
    };

    private readonly double? acuUsdRate;

    /// <summary>
    /// Initializes a new <see cref="DevinMapper"/>.
    /// </summary>
    /// <param name="acuUsdRate">
    /// Configured <c>devin.acuUsdRate</c> converting committed ACUs to USD.
    /// When <see langword="null"/> (or non-positive/non-finite), committed ACUs stay
    /// unreported instead of pricing at a measured <c>$0</c>.
    /// </param>
    public DevinMapper(double? acuUsdRate = null)
    {
        this.acuUsdRate = acuUsdRate;
    }

    /// <inheritdoc />
    public string HarnessId => DevinHarnessId;

    /// <summary>
    /// Parses raw Devin CLI agent-trajectory transcript JSON into a strongly typed
    /// <see cref="DevinTranscriptDocument"/>. Never throws: malformed roots yield an
    /// invalid document carrying a <see cref="DevinParseProblem"/>.
    /// </summary>
    /// <param name="rawText">Raw UTF-8 text of the transcript file.</param>
    /// <returns>The parsed transcript document.</returns>
    public static DevinTranscriptDocument Parse(string? rawText)
    {
        if (string.IsNullOrWhiteSpace(rawText))
        {
            return Invalid("Skipping empty Devin transcript: expected a JSON object with a steps array.");
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(rawText);
        }
        catch (JsonException ex)
        {
            return Invalid($"Skipping malformed Devin transcript: {ex.Message}");
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return Invalid("Skipping Devin transcript: expected a top-level JSON object.");
            }

            JsonElement root = document.RootElement;
            string? sessionId = ReadOptionalString(root, "session_id");
            string? agentModelName = root.TryGetProperty("agent", out JsonElement agentEl)
                && agentEl.ValueKind == JsonValueKind.Object
                ? ReadOptionalString(agentEl, "model_name")
                : null;

            if (root.TryGetProperty("message_nodes", out JsonElement nodesEl)
                && nodesEl.ValueKind == JsonValueKind.Array)
            {
                return ParseMessageNodes(root, nodesEl, sessionId);
            }

            if (!root.TryGetProperty("steps", out JsonElement stepsEl)
                || stepsEl.ValueKind != JsonValueKind.Array)
            {
                return Invalid("Skipping Devin transcript: expected a steps JSON array.");
            }

            List<DevinStep> steps = [];
            List<DevinParseProblem> problems = [];
            int index = 0;
            foreach (JsonElement stepEl in stepsEl.EnumerateArray())
            {
                index++;
                if (stepEl.ValueKind != JsonValueKind.Object)
                {
                    problems.Add(new DevinParseProblem(
                        LineNumber: index,
                        Code: MalformedDocumentCode,
                        Message: string.Create(CultureInfo.InvariantCulture, $"Skipping non-object Devin step {index}.")));
                    continue;
                }

                steps.Add(ParseStep(stepEl, index));
            }

            return new DevinTranscriptDocument(
                IsValid: true,
                SessionId: sessionId,
                AgentModelName: agentModelName,
                Steps: steps,
                ParseProblems: problems);
        }
    }

    /// <inheritdoc />
    public CollectedBatch Map(NativeSourceUnit unit, DateTimeOffset importedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(unit);
        ArgumentNullException.ThrowIfNull(unit.Unit);

        CollectedSource source = new(DevinHarnessId, unit.Unit.Kind, unit.Unit.SourceKey);
        if (unit.RawText is null)
        {
            return new CollectedBatch(
                source,
                [],
                [],
                BuildProblems(
                    [new DevinParseProblem(0, MalformedDocumentCode, $"Skipping Devin source {unit.Unit.SourceKey}: the reader supplied no payload (null).")],
                    unit.Unit.SourceKey,
                    importedAtUtc));
        }

        if (string.IsNullOrWhiteSpace(unit.RawText))
        {
            return new CollectedBatch(source, [], [], []);
        }

        DevinTranscriptDocument document = Parse(unit.RawText);
        List<CollectedProblem> problems = BuildProblems(
            document.ParseProblems, unit.Unit.SourceKey, importedAtUtc);

        if (!document.IsValid)
        {
            return new CollectedBatch(source, [], [], problems);
        }

        string sessionId = ResolveSessionId(document.SessionId, unit.Unit.SourceKey);
        string traceId = string.Create(CultureInfo.InvariantCulture, $"{CanonSynth.SynthSpanPrefix}{DevinHarnessId}:{sessionId}");
        DateTimeOffset fallbackTimestamp = unit.LastWriteUtc == default ? importedAtUtc : unit.LastWriteUtc;

        List<CollectedRecord> records = [];
        HashSet<string> seenAnchors = new(StringComparer.Ordinal);
        HashSet<string> seenRecordIds = new(StringComparer.Ordinal);

        for (int index = 0; index < document.Steps.Count; index++)
        {
            DevinStep step = document.Steps[index];
            if (step.Metadata?.IsUserInput == true)
            {
                continue;
            }

            DevinStepUsage? usage = GetUsage(step);
            if (usage is null && document.FromMessageNodes)
            {
                // D9: an assistant node without metrics is still a call; it carries no token rows.
                usage = new DevinStepUsage(0, null, null, null, null);
            }

            if (usage is null)
            {
                continue;
            }

            string anchor = string.IsNullOrWhiteSpace(step.StepId)
                ? string.Create(CultureInfo.InvariantCulture, $"step-{index + 1}")
                : step.StepId;
            string deduplicationKey = string.Create(CultureInfo.InvariantCulture, $"{DevinHarnessId}:{sessionId}:{anchor}");
            if (!seenAnchors.Add(deduplicationKey))
            {
                continue;
            }

            DateTimeOffset startedAt = TryParseTimestamp(step.Metadata?.CreatedAt) ?? fallbackTimestamp;
            string model = GetDisplayModelName(
                FirstPresentString(step.Extra?.GenerationModel, step.Metadata?.GenerationModel),
                FirstPresentString(step.ModelName, document.AgentModelName) ?? DefaultModelName);
            string? userMessage = GetFirstUserMessageBeforeStep(document.Steps, index);

            string parentRecordId = EnsureUniqueRecordId(
                seenRecordIds,
                string.Create(CultureInfo.InvariantCulture, $"{traceId}:{anchor}"),
                index + 1);

            List<CollectedContentPart> parts = [];
            string? correctionRule = null;
            if (!string.IsNullOrEmpty(userMessage))
            {
                CorrectionDetectionResult check = CanonFindings.DetectUserCorrection(userMessage);
                if (check.Matched)
                {
                    correctionRule = check.Rule;
                }

                parts.Add(ToolContentMeasurer.BuildContentPart(0, ContentBucket.UserMessage, userMessage, "user", model));
            }

            (IReadOnlyList<CollectedTokenCount> tokens, long? reportedInput, long? reportedOutput) = BuildTokens(usage);
            CollectedCost? reportedCost = BuildReportedCost(usage.CommittedAcuCost);

            CollectedRecord parentRecord = new(
                parentRecordId,
                DevinHarnessId,
                RecordOp.LlmInvoke,
                RecordKind.Internal,
                string.Create(CultureInfo.InvariantCulture, $"{DevinHarnessId}:{model}"),
                startedAt,
                RecordStatus.Ok,
                sessionId,
                null,
                traceId,
                model,
                null,
                ToReportedDurationMs(step.Metadata?.TotalTimeMs),
                tokens,
                reportedCost,
                parts,
                null,
                null,
                [],
                null,
                [],
                new CollectedRecordSignals(null, null, correctionRule, null),
                reportedInput,
                reportedOutput);

            records.Add(parentRecord);

            for (int t = 0; t < step.ToolCalls.Count; t++)
            {
                DevinToolCall toolCall = step.ToolCalls[t];
                if (string.IsNullOrWhiteSpace(toolCall.FunctionName))
                {
                    continue;
                }

                records.Add(BuildToolInvokeRecord(parentRecord, sessionId, traceId, toolCall, t, seenRecordIds));
            }

        }

        List<CollectedSession> sessions =
        [
            new CollectedSession(
                SessionKey: sessionId,
                HarnessId: DevinHarnessId,
                NativeSessionId: sessionId,
                Workspace: SafeOptionalText(document.WorkingDirectory) is { } workingDirectory
                    ? new CollectedWorkspace(workingDirectory, null, null)
                    : null,
                Title: SafeOptionalText(document.Title),
                ReportedUsage: []),
        ];

        return new CollectedBatch(source, sessions, records, problems);
    }

    /// <summary>
    /// Resolves the Devin display model name from the backend generation model and the
    /// transcript model fallback, porting <c>getDevinDisplayModelName</c>: <c>MODEL_*</c>
    /// placeholders fall back, <c>gpt-*</c> ids normalize dash versions and surface effort
    /// tiers, and all other ids resolve through the offline short-name table.
    /// </summary>
    /// <param name="generationModel">Backend generation model (<c>metadata/extra.generation_model</c>).</param>
    /// <param name="modelName">Transcript model fallback (<c>step/agent/session model_name</c>).</param>
    /// <returns>The display model name.</returns>
    public static string GetDisplayModelName(string? generationModel, string? modelName)
    {
        string fallback = FirstPresentString(modelName) ?? DefaultModelName;
        string? generation = FirstPresentString(generationModel);
        if (generation is null || generation.StartsWith("MODEL_", StringComparison.Ordinal))
        {
            return DevinModelShortNames.ResolveShortName(fallback);
        }

        if (generation.StartsWith("gpt-", StringComparison.Ordinal))
        {
            string normalized = GptMinorVersionRegex().Replace(generation, "gpt-$1.$2");
            Match effortMatch = EffortTierRegex().Match(normalized);
            string? effort = effortMatch.Success && EffortTiers.Contains(effortMatch.Groups[1].Value)
                ? effortMatch.Groups[1].Value
                : null;
            string bare = effort is null
                ? normalized
                : normalized.Substring(0, normalized.Length - (effort.Length + 1));
            string friendly = GetFriendlyGptName(bare);
            return effort is null
                ? friendly
                : string.Create(CultureInfo.InvariantCulture, $"{friendly} ({effort})");
        }

        return DevinModelShortNames.ResolveShortName(generation);
    }

    /// <summary>
    /// Renders a bare <c>gpt-*</c> model id as a friendly display name, reconstructing
    /// <c>GPT-&lt;major&gt;[.&lt;minor&gt;] [&lt;Words&gt;]</c> when the short-name table
    /// has no better label. Dated snapshots (numeric suffix tokens) pass through raw.
    /// </summary>
    /// <param name="model">Bare <c>gpt-*</c> model id.</param>
    /// <returns>The friendly display name.</returns>
    public static string GetFriendlyGptName(string model)
    {
        ArgumentNullException.ThrowIfNull(model);
        string shortName = DevinModelShortNames.ResolveShortName(model);
        Match match = GptNameRegex().Match(model);
        if (!match.Success)
        {
            return shortName;
        }

        string version = match.Groups[1].Value;
        string suffix = match.Groups[2].Success ? match.Groups[2].Value : string.Empty;
        string[] suffixParts = suffix.Split('-', StringSplitOptions.RemoveEmptyEntries);
        if (suffixParts.Any(static part => part.Length > 0 && part.All(static c => c >= '0' && c <= '9')))
        {
            return shortName;
        }

        string titled = string.Join(' ', suffixParts.Select(static part =>
            string.Create(CultureInfo.InvariantCulture, $"{char.ToUpperInvariant(part[0])}{part.AsSpan(1)}")));
        string reconstructed = titled.Length == 0
            ? string.Create(CultureInfo.InvariantCulture, $"GPT-{version}")
            : string.Create(CultureInfo.InvariantCulture, $"GPT-{version} {titled}");
        if (!string.Equals(shortName, model, StringComparison.Ordinal)
            && (titled.Length == 0 || !string.Equals(shortName, string.Create(CultureInfo.InvariantCulture, $"GPT-{version}"), StringComparison.Ordinal)))
        {
            return shortName;
        }

        return reconstructed;
    }

    private static DevinTranscriptDocument Invalid(string message) =>
        new(
            IsValid: false,
            SessionId: null,
            AgentModelName: null,
            Steps: [],
            ParseProblems: [new DevinParseProblem(0, MalformedDocumentCode, message)]);

    /// <summary>
    /// Parses the Devin <c>sessions.db</c> export (a root holding a <c>message_nodes</c> array) into
    /// the transcript model: <c>user</c> nodes become user-input steps, <c>assistant</c> nodes become
    /// steps with metrics from <c>metadata.metrics</c>, and <c>tool</c> and <c>system</c> nodes emit
    /// no step. A node whose <c>chat_message</c> is not an object, or whose role is unknown, is one
    /// problem; the other nodes still map.
    /// </summary>
    private static DevinTranscriptDocument ParseMessageNodes(JsonElement root, JsonElement nodesEl, string? sessionId)
    {
        string? exportModel = ReadOptionalString(root, "model");
        List<DevinStep> steps = [];
        List<DevinParseProblem> problems = [];
        int index = 0;
        foreach (JsonElement nodeEl in nodesEl.EnumerateArray())
        {
            index++;
            if (nodeEl.ValueKind != JsonValueKind.Object
                || !nodeEl.TryGetProperty("chat_message", out JsonElement chatEl)
                || chatEl.ValueKind != JsonValueKind.Object)
            {
                problems.Add(new DevinParseProblem(
                    index,
                    MalformedDocumentCode,
                    string.Create(CultureInfo.InvariantCulture, $"Skipping Devin message node {index}: chat_message is not an object.")));
                continue;
            }

            string? role = ReadOptionalString(chatEl, "role");
            string stepId = ReadOptionalString(chatEl, "message_id")
                ?? ReadOptionalString(nodeEl, "node_id")
                ?? string.Create(CultureInfo.InvariantCulture, $"step-{index}");
            string? content = chatEl.TryGetProperty("content", out JsonElement contentEl)
                ? NormalizeMessageValue(contentEl)
                : null;

            switch (role)
            {
                case "tool":
                case "system":
                    break;
                case "user":
                    steps.Add(new DevinStep(
                        stepId,
                        role,
                        null,
                        content,
                        [],
                        null,
                        null,
                        new DevinStepMetadata(null, null, null, true, null, null, null, null)));
                    break;
                case "assistant":
                    steps.Add(ParseAssistantNode(chatEl, stepId, content, exportModel));
                    break;
                default:
                    problems.Add(new DevinParseProblem(
                        index,
                        MalformedDocumentCode,
                        string.Create(CultureInfo.InvariantCulture, $"Skipping Devin message node {index}: unrecognised role.")));
                    break;
            }
        }

        return new DevinTranscriptDocument(
            IsValid: true,
            SessionId: sessionId,
            AgentModelName: exportModel,
            Steps: steps,
            ParseProblems: problems,
            Title: ReadOptionalString(root, "title"),
            WorkingDirectory: ReadOptionalString(root, "working_directory"),
            FromMessageNodes: true);
    }

    private static long? ToReportedDurationMs(double? totalTimeMs) =>
        totalTimeMs is { } value && double.IsFinite(value) && value >= 0 && value <= long.MaxValue
            ? (long)Math.Round(value, MidpointRounding.AwayFromZero)
            : null;

    private static DevinStep ParseAssistantNode(JsonElement chatEl, string stepId, string? content, string? exportModel)
    {
        List<DevinToolCall> toolCalls = [];
        if (chatEl.TryGetProperty("tool_calls", out JsonElement toolsEl)
            && toolsEl.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement toolEl in toolsEl.EnumerateArray())
            {
                string? name = toolEl.ValueKind == JsonValueKind.Object ? ReadOptionalString(toolEl, "name") : null;
                if (string.IsNullOrWhiteSpace(name))
                {
                    continue;
                }

                string? argumentsJson = toolEl.TryGetProperty("arguments", out JsonElement argsEl)
                    && argsEl.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined)
                    ? argsEl.GetRawText()
                    : null;
                toolCalls.Add(new DevinToolCall(ReadOptionalString(toolEl, "id"), name, argumentsJson));
            }
        }

        string? createdAt = null;
        string? generationModel = null;
        double? input = null;
        double? output = null;
        double? cacheCreation = null;
        double? cacheRead = null;
        double? totalTimeMs = null;
        if (chatEl.TryGetProperty("metadata", out JsonElement metadataEl)
            && metadataEl.ValueKind == JsonValueKind.Object)
        {
            createdAt = ReadStringOnly(metadataEl, "created_at");
            generationModel = ReadOptionalString(metadataEl, "generation_model");
            if (metadataEl.TryGetProperty("metrics", out JsonElement metricsEl)
                && metricsEl.ValueKind == JsonValueKind.Object)
            {
                input = ReadOptionalDouble(metricsEl, "input_tokens");
                output = ReadOptionalDouble(metricsEl, "output_tokens");
                cacheCreation = ReadOptionalDouble(metricsEl, "cache_creation_tokens");
                cacheRead = ReadOptionalDouble(metricsEl, "cache_read_tokens");
                totalTimeMs = ReadOptionalDouble(metricsEl, "total_time_ms");
            }
        }

        return new DevinStep(
            stepId,
            "assistant",
            generationModel ?? exportModel,
            content,
            toolCalls,
            null,
            null,
            new DevinStepMetadata(createdAt, null, generationModel, false, input, output, cacheCreation, cacheRead, totalTimeMs));
    }

    private static DevinStep ParseStep(JsonElement stepEl, int index)
    {
        string stepId = ReadStepId(stepEl, index);
        string? source = ReadOptionalString(stepEl, "source");
        string? modelName = ReadOptionalString(stepEl, "model_name");
        string? messageText = NormalizeStepMessage(stepEl);

        List<DevinToolCall> toolCalls = [];
        if (stepEl.TryGetProperty("tool_calls", out JsonElement toolsEl)
            && toolsEl.ValueKind == JsonValueKind.Array)
        {
            int position = 0;
            foreach (JsonElement toolEl in toolsEl.EnumerateArray())
            {
                position++;
                if (toolEl.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                string? functionName = ReadOptionalString(toolEl, "function_name");
                if (string.IsNullOrWhiteSpace(functionName))
                {
                    continue;
                }

                string? callId = ReadOptionalString(toolEl, "tool_call_id");
                string? argumentsJson = null;
                if (toolEl.TryGetProperty("arguments", out JsonElement argsEl)
                    && argsEl.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined))
                {
                    argumentsJson = argsEl.GetRawText();
                }

                toolCalls.Add(new DevinToolCall(callId, functionName, argumentsJson));
            }
        }

        DevinStepExtra? extra = null;
        if (stepEl.TryGetProperty("extra", out JsonElement extraEl)
            && extraEl.ValueKind == JsonValueKind.Object)
        {
            double? extraAcu = ReadOptionalDouble(extraEl, "committed_acu_cost");
            string? extraModel = ReadOptionalString(extraEl, "generation_model");
            if (extraAcu.HasValue || extraModel is not null)
            {
                extra = new DevinStepExtra(extraAcu, extraModel);
            }
        }

        DevinStepMetrics? metrics = null;
        if (stepEl.TryGetProperty("metrics", out JsonElement metricsEl)
            && metricsEl.ValueKind == JsonValueKind.Object)
        {
            double? prompt = ReadOptionalDouble(metricsEl, "prompt_tokens");
            double? completion = ReadOptionalDouble(metricsEl, "completion_tokens");
            double? cached = ReadOptionalDouble(metricsEl, "cached_tokens");
            double? cacheCreation = null;
            if (metricsEl.TryGetProperty("extra", out JsonElement metricsExtraEl)
                && metricsExtraEl.ValueKind == JsonValueKind.Object)
            {
                cacheCreation = ReadOptionalDouble(metricsExtraEl, "cache_creation_input_tokens");
            }

            if (prompt.HasValue || completion.HasValue || cached.HasValue || cacheCreation.HasValue)
            {
                metrics = new DevinStepMetrics(prompt, completion, cached, cacheCreation);
            }
        }

        DevinStepMetadata? metadata = null;
        if (stepEl.TryGetProperty("metadata", out JsonElement metadataEl)
            && metadataEl.ValueKind == JsonValueKind.Object)
        {
            double? input = null;
            double? output = null;
            double? cacheCreation = null;
            double? cacheRead = null;
            if (metadataEl.TryGetProperty("metrics", out JsonElement legacyMetricsEl)
                && legacyMetricsEl.ValueKind == JsonValueKind.Object)
            {
                input = ReadOptionalDouble(legacyMetricsEl, "input_tokens");
                output = ReadOptionalDouble(legacyMetricsEl, "output_tokens");
                cacheCreation = ReadOptionalDouble(legacyMetricsEl, "cache_creation_tokens");
                cacheRead = ReadOptionalDouble(legacyMetricsEl, "cache_read_tokens");
            }

            bool isUserInput = metadataEl.TryGetProperty("is_user_input", out JsonElement userInputEl)
                && userInputEl.ValueKind == JsonValueKind.True;

            metadata = new DevinStepMetadata(
                CreatedAt: ReadStringOnly(metadataEl, "created_at"),
                CommittedAcuCost: ReadOptionalDouble(metadataEl, "committed_acu_cost"),
                GenerationModel: ReadOptionalString(metadataEl, "generation_model"),
                IsUserInput: isUserInput,
                InputTokens: input,
                OutputTokens: output,
                CacheCreationTokens: cacheCreation,
                CacheReadTokens: cacheRead);
        }

        return new DevinStep(
            StepId: stepId,
            Source: source,
            ModelName: modelName,
            MessageText: messageText,
            ToolCalls: toolCalls,
            Extra: extra,
            Metrics: metrics,
            Metadata: metadata);
    }

    private static string ReadStepId(JsonElement stepEl, int index)
    {
        if (stepEl.TryGetProperty("step_id", out JsonElement idEl))
        {
            if (idEl.ValueKind == JsonValueKind.String)
            {
                string? text = idEl.GetString()?.Trim();
                if (!string.IsNullOrEmpty(text))
                {
                    return text;
                }
            }
            else if (idEl.ValueKind == JsonValueKind.Number)
            {
                return idEl.GetRawText();
            }
            else if (idEl.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined))
            {
                string raw = idEl.GetRawText().Trim();
                if (raw.Length > 0)
                {
                    return raw;
                }
            }
        }

        return string.Create(CultureInfo.InvariantCulture, $"step-{index}");
    }

    private static string? NormalizeStepMessage(JsonElement stepEl)
    {
        return stepEl.TryGetProperty("message", out JsonElement messageEl)
            ? NormalizeMessageValue(messageEl)
            : null;
    }

    private static string? NormalizeMessageValue(JsonElement messageEl)
    {
        if (messageEl.ValueKind == JsonValueKind.String)
        {
            string? text = messageEl.GetString()?.Trim();
            return string.IsNullOrEmpty(text) ? null : text;
        }

        if (messageEl.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        List<string> parts = [];
        foreach (JsonElement partEl in messageEl.EnumerateArray())
        {
            if (partEl.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            string? partType = ReadOptionalString(partEl, "type");
            if (string.Equals(partType, "text", StringComparison.Ordinal))
            {
                parts.Add(ReadOptionalString(partEl, "text")?.Trim() ?? string.Empty);
            }
            else if (string.Equals(partType, "image", StringComparison.Ordinal)
                && partEl.TryGetProperty("source", out JsonElement sourceEl)
                && sourceEl.ValueKind == JsonValueKind.Object)
            {
                string? path = ReadOptionalString(sourceEl, "path");
                if (path is not null)
                {
                    parts.Add(path);
                }
            }
        }

        string joined = string.Join(' ', parts);
        return joined.Length == 0 ? null : joined;
    }

    private sealed record DevinStepUsage(
        double CommittedAcuCost,
        double? InputTokens,
        double? OutputTokens,
        double? CacheCreationInputTokens,
        double? CacheReadInputTokens);

    private static DevinStepUsage? GetUsage(DevinStep step)
    {
        double committedAcuCost = FirstPositive(
            step.Metadata?.CommittedAcuCost,
            step.Extra?.CommittedAcuCost);

        DevinStepMetrics? metrics = HasAnyTokenField(step.Metrics)
            ? step.Metrics
            : GetDevinMetricsFromMetadata(step.Metadata);

        double? prompt = metrics?.PromptTokens;
        double? completion = metrics?.CompletionTokens;
        double? cacheCreation = metrics?.CacheCreationInputTokens;
        double? cached = metrics?.CachedTokens;

        bool hasAnyUsage = IsPositiveNumber(committedAcuCost)
            || IsPositiveNumber(prompt)
            || IsPositiveNumber(completion)
            || IsPositiveNumber(cacheCreation)
            || IsPositiveNumber(cached);
        if (!hasAnyUsage)
        {
            return null;
        }

        return new DevinStepUsage(committedAcuCost, prompt, completion, cacheCreation, cached);
    }

    private static bool HasAnyTokenField(DevinStepMetrics? metrics)
    {
        if (metrics is null)
        {
            return false;
        }

        return metrics.PromptTokens.HasValue
            || metrics.CompletionTokens.HasValue
            || metrics.CachedTokens.HasValue
            || metrics.CacheCreationInputTokens.HasValue;
    }

    private static DevinStepMetrics? GetDevinMetricsFromMetadata(DevinStepMetadata? metadata)
    {
        if (metadata is null)
        {
            return null;
        }

        if (!metadata.InputTokens.HasValue
            && !metadata.OutputTokens.HasValue
            && !metadata.CacheCreationTokens.HasValue
            && !metadata.CacheReadTokens.HasValue)
        {
            return null;
        }

        return new DevinStepMetrics(
            PromptTokens: metadata.InputTokens,
            CompletionTokens: metadata.OutputTokens,
            CachedTokens: metadata.CacheReadTokens,
            CacheCreationInputTokens: metadata.CacheCreationTokens);
    }

    private static string? GetFirstUserMessageBeforeStep(IReadOnlyList<DevinStep> steps, int index)
    {
        for (int i = index - 1; i >= 0; i--)
        {
            DevinStep step = steps[i];
            if (step.Metadata?.IsUserInput != true)
            {
                continue;
            }

            if (!string.IsNullOrEmpty(step.MessageText))
            {
                return step.MessageText;
            }
        }

        return null;
    }

    private CollectedCost? BuildReportedCost(double committedAcuCost)
    {
        if (!IsPositiveNumber(this.acuUsdRate) || !IsPositiveNumber(committedAcuCost))
        {
            return null;
        }

        return new CollectedCost(committedAcuCost * this.acuUsdRate!.Value, "USD");
    }

    private static (IReadOnlyList<CollectedTokenCount> Tokens, long? ReportedInput, long? ReportedOutput) BuildTokens(DevinStepUsage usage)
    {
        List<CollectedTokenCount> tokens = [];
        long inputSum = 0L;
        bool hasInput = false;

        if (IsPositiveNumber(usage.InputTokens))
        {
            tokens.Add(new CollectedTokenCount(TokenClass.Input, (long)usage.InputTokens!.Value));
            inputSum += (long)usage.InputTokens!.Value;
            hasInput = true;
        }

        if (IsPositiveNumber(usage.CacheReadInputTokens))
        {
            tokens.Add(new CollectedTokenCount(TokenClass.CacheRead, (long)usage.CacheReadInputTokens!.Value));
            inputSum += (long)usage.CacheReadInputTokens!.Value;
            hasInput = true;
        }

        if (IsPositiveNumber(usage.CacheCreationInputTokens))
        {
            tokens.Add(new CollectedTokenCount(TokenClass.CacheWrite, (long)usage.CacheCreationInputTokens!.Value));
            inputSum += (long)usage.CacheCreationInputTokens!.Value;
            hasInput = true;
        }

        if (IsPositiveNumber(usage.OutputTokens))
        {
            tokens.Add(new CollectedTokenCount(TokenClass.Output, (long)usage.OutputTokens!.Value));
        }

        return (tokens, hasInput ? inputSum : null, IsPositiveNumber(usage.OutputTokens) ? (long)usage.OutputTokens!.Value : null);
    }

    private static CollectedRecord BuildToolInvokeRecord(
        CollectedRecord parentRecord,
        string sessionId,
        string traceId,
        DevinToolCall toolCall,
        int toolIndex,
        HashSet<string> seenRecordIds)
    {
        string safeCallId = string.IsNullOrWhiteSpace(toolCall.CallId)
            ? string.Create(CultureInfo.InvariantCulture, $"call-{toolIndex}")
            : toolCall.CallId.Trim();
        string rawInput = toolCall.ArgumentsJson ?? "{}";

        byte[] hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(
            string.Create(CultureInfo.InvariantCulture, $"{safeCallId}:{toolCall.FunctionName}:{rawInput}")));
        string toolHash = Convert.ToHexStringLower(hashBytes)[..12];
        string childRecordId = EnsureUniqueRecordId(
            seenRecordIds,
            string.Create(CultureInfo.InvariantCulture, $"{parentRecord.RecordId}-t{toolHash}"),
            toolIndex);

        CollectedToolContent? inputContent = toolCall.ArgumentsJson is not null
            ? ToolContentMeasurer.Measure(toolCall.ArgumentsJson, CanonSynth.MaxToolArgumentsBytes, parentRecord.Model)
                .ToToolContent(includeFingerprint: true)
            : null;

        CollectedToolCall collectedTool = new(
            safeCallId,
            toolCall.FunctionName,
            null,
            null,
            inputContent,
            null,
            null,
            null,
            null);

        return new CollectedRecord(
            childRecordId,
            DevinHarnessId,
            RecordOp.ToolInvoke,
            RecordKind.Internal,
            toolCall.FunctionName,
            parentRecord.StartedAt,
            RecordStatus.Unset,
            sessionId,
            parentRecord.RecordId,
            traceId,
            null,
            null,
            null,
            [],
            null,
            [],
            collectedTool,
            null,
            [],
            null,
            [],
            new CollectedRecordSignals(null, null, null, null));
    }

    private static string ResolveSessionId(string? parsedSessionId, string sourceKey)
    {
        string? explicitId = SafeOptionalText(parsedSessionId);
        if (explicitId is not null)
        {
            return explicitId;
        }

        string? fileStem = SafeOptionalText(Path.GetFileNameWithoutExtension(sourceKey));
        if (fileStem is not null)
        {
            return fileStem;
        }

        byte[] digest = SHA256.HashData(Encoding.UTF8.GetBytes(sourceKey));
        return string.Create(CultureInfo.InvariantCulture, $"devin-{Convert.ToHexStringLower(digest)[..16]}");
    }

    private static string EnsureUniqueRecordId(HashSet<string> seenRecordIds, string candidate, int disambiguator) =>
        HarnessJson.EnsureUniqueRecordId(seenRecordIds, candidate, disambiguator);

    private static DateTimeOffset? TryParseTimestamp(string? rawTimestamp) =>
        HarnessJson.TryParseTimestamp(rawTimestamp);

    private static List<CollectedProblem> BuildProblems(
        IReadOnlyList<DevinParseProblem> parseProblems,
        string sourceKey,
        DateTimeOffset importedAtUtc)
    {
        if (parseProblems.Count == 0)
        {
            return [];
        }

        string timestampUtc = importedAtUtc.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
        List<CollectedProblem> problems = new(parseProblems.Count);

        foreach (DevinParseProblem parseProblem in parseProblems)
        {
            string problemKey = string.Create(
                CultureInfo.InvariantCulture,
                $"devin-malformed:{sourceKey}:step-{parseProblem.LineNumber}");
            string location = string.Create(CultureInfo.InvariantCulture, $"{sourceKey}:{parseProblem.LineNumber}");
            string message = SafeOptionalText(parseProblem.Message) ?? "Malformed Devin transcript.";

            problems.Add(new CollectedProblem(
                problemKey,
                "warning",
                parseProblem.Code,
                message,
                null,
                location,
                null,
                DevinHarnessId,
                timestampUtc,
                null));
        }

        return problems;
    }

    private static double FirstPositive(params double?[] values)
    {
        foreach (double? value in values)
        {
            if (IsPositiveNumber(value))
            {
                return value!.Value;
            }
        }

        return 0;
    }

    private static bool IsPositiveNumber(double? value) =>
        value.HasValue && double.IsFinite(value.Value) && value.Value > 0;

    private static string? FirstPresentString(params string?[] values) =>
        HarnessJson.FirstPresentString(values);

    private static string? ReadOptionalString(JsonElement obj, string key) =>
        HarnessJson.ReadOptionalString(obj, key);

    private static string? ReadStringOnly(JsonElement obj, string key) =>
        HarnessJson.ReadStringOnly(obj, key);

    private static double? ReadOptionalDouble(JsonElement obj, string key) =>
        HarnessJson.ReadOptionalDouble(obj, key);

    private static string? SafeOptionalText(string? value) =>
        HarnessJson.SafeOptionalText(value);

    [GeneratedRegex(@"^gpt-(\d+)-(\d)(?=-|$)", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex GptMinorVersionRegex();

    [GeneratedRegex(@"-([^-]+)$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex EffortTierRegex();

    [GeneratedRegex(@"^gpt-(\d+(?:\.\d+)*)(?:-(.+))?$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex GptNameRegex();

}
