using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace KyberDash.Domain;

/// <summary>Skill entry carried inside <see cref="ListedSkillsPayload"/> (<c>kyber.skills.listed</c>).</summary>
/// <param name="Name">Declared skill identifier.</param>
/// <param name="Description">Optional skill description when supplied by the transcript.</param>
public record ListedSkillItem(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("description"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Description = null);

/// <summary>Skill listing attachment carried onto <c>raw["kyber.skills.listed"]</c>.</summary>
/// <param name="IsInitial">
/// True when this listing was the session's initial skill announcement; <see langword="null"/> when
/// the source did not keep the flag (a listing hydrated from <c>record_skill_listing</c>), and then
/// the key is left out of the attribute.
/// </param>
/// <param name="Skills">Declared skills visible in the listing.</param>
public record ListedSkillsPayload(
    [property: JsonPropertyName("isInitial"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? IsInitial,
    [property: JsonPropertyName("skills")] IReadOnlyList<ListedSkillItem> Skills);

/// <summary>Normalized model call emitted by a harness/provider session file parser (<c>dash/src/providers/types.ts</c>).</summary>
public record ParsedProviderCall
{
    /// <summary>ISO-8601 UTC timestamp of the model invocation.</summary>
    public required string Timestamp { get; init; }

    /// <summary>Upstream provider or parser identifier (for example <c>"claude"</c>, <c>"codex"</c>, <c>"copilot"</c>).</summary>
    public required string Provider { get; init; }

    /// <summary>Reported model identifier.</summary>
    public required string Model { get; init; }

    /// <summary>Exclusive uncached input token count.</summary>
    public long InputTokens { get; init; }

    /// <summary>Output token count.</summary>
    public long OutputTokens { get; init; }

    /// <summary>Cache-read input token count (authoritative field).</summary>
    public long CacheReadInputTokens { get; init; }

    /// <summary>Cache-creation input token count.</summary>
    public long CacheCreationInputTokens { get; init; }

    /// <summary>Legacy mirror of cache-read tokens used only when <see cref="CacheReadInputTokens"/> is zero.</summary>
    public long CachedInputTokens { get; init; }

    /// <summary>Reasoning/thinking token count reported by the provider, when non-zero.</summary>
    public long ReasoningTokens { get; init; }

    /// <summary>Harness-reported USD cost, or a cost calculated from published rates (<c>0</c> means unrated/no rate).</summary>
    public double CostUsd { get; init; }

    /// <summary>True when <see cref="CostUsd"/> was calculated from published rates (a bundled rate table) rather than harness billing.</summary>
    public bool CostIsCalculated { get; init; }

    /// <summary>True when a Copilot reader observed a genuine harness-reported cost figure.</summary>
    public bool CostHarnessReported { get; init; }

    /// <summary>Native session identifier, or <see langword="null"/> when omitted.</summary>
    public string? SessionId { get; init; }

    /// <summary>Upstream deduplication key (<c>&lt;provider&gt;:&lt;sessionId&gt;:&lt;messageId&gt;</c>).</summary>
    public string DeduplicationKey { get; init; } = string.Empty;

    /// <summary>Native turn/message identifier when reported by the parser.</summary>
    public string? TurnId { get; init; }

    /// <summary>Explicit native record identity when separate from <see cref="TurnId"/>.</summary>
    public string? RecordIdentity { get; init; }

    /// <summary>Copilot initiator tag (for example <c>"compaction"</c>).</summary>
    public string? Initiator { get; init; }

    /// <summary>Active turn duration in milliseconds when measured by the session parser.</summary>
    public long? ActiveDurationMs { get; init; }

    /// <summary>User-assigned or transcript-derived session title.</summary>
    public string? SessionTitle { get; init; }

    /// <summary>Tool names invoked in this call.</summary>
    public IReadOnlyList<string>? Tools { get; init; }

    /// <summary>Bash commands observed in this call.</summary>
    public IReadOnlyList<string>? BashCommands { get; init; }

    /// <summary>Ordered sequence of tool invocations observed in this call.</summary>
    public IReadOnlyList<string>? ToolSequence { get; init; }

    /// <summary>Number of web search requests issued during the call.</summary>
    public int? WebSearchRequests { get; init; }

    /// <summary>Skills invoked during the turn (Antigravity preserves empty lists as measured none).</summary>
    public IReadOnlyList<string>? Skills { get; init; }

    /// <summary>Skill listing attachment carried by the turn.</summary>
    public ListedSkillsPayload? SkillsListed { get; init; }
}

/// <summary>Provenance metadata stamped onto <c>records.raw["provenance"]</c> for checkpointed refresh.</summary>
public record SourceRecordProvenance(
    [property: JsonPropertyName("harnessId")] string HarnessId,
    [property: JsonPropertyName("sourceKey")] string SourceKey,
    [property: JsonPropertyName("nativeSessionId"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? NativeSessionId,
    [property: JsonPropertyName("nativeRecordId")] string NativeRecordId,
    [property: JsonPropertyName("recordDigest"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? RecordDigest = null,
    [property: JsonPropertyName("sourceRevision"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? SourceRevision = null,
    [property: JsonPropertyName("parserContractVersion"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ParserContractVersion = null,
    [property: JsonPropertyName("importedAt"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ImportedAt = null,
    [property: JsonPropertyName("locationToken"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? LocationToken = null,
    [property: JsonPropertyName("format"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Format = null,
    [property: JsonPropertyName("parserId"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ParserId = null);

/// <summary>Structured tool invocation extracted by a session content reader.</summary>
public record ReaderToolCall
{
    /// <summary>Tool call correlation identifier.</summary>
    public required string Id { get; init; }

    /// <summary>Invoked tool name.</summary>
    public required string Name { get; init; }

    /// <summary>Structured JSON object arguments, when arguments are structured.</summary>
    public JsonElement? Arguments { get; init; }

    /// <summary>Raw string arguments when tool arguments arrived as an unparsed or primitive string.</summary>
    public string? RawArgumentsString { get; init; }

    /// <summary>ISO-8601 UTC timestamp when the tool invocation started, if recorded.</summary>
    public string? Timestamp { get; init; }

    /// <summary>Measured tool duration in milliseconds, if recorded.</summary>
    public long? DurationMs { get; init; }
}

/// <summary>Structured tool execution result paired with a <see cref="ReaderToolCall"/>.</summary>
public record ReaderToolResult
{
    /// <summary>Tool call correlation identifier matching <see cref="ReaderToolCall.Id"/>.</summary>
    public required string ToolCallId { get; init; }

    /// <summary>Optional tool name reported alongside the result.</summary>
    public string? Name { get; init; }

    /// <summary>Result payload text.</summary>
    public required string Content { get; init; }

    /// <summary>True when the tool execution terminated in error.</summary>
    public bool IsError { get; init; }

    /// <summary>ISO-8601 UTC timestamp when the tool result completed, if recorded.</summary>
    public string? Timestamp { get; init; }
}

/// <summary>One model turn's extracted transcript content and outcome signals.</summary>
public record ReaderTurn
{
    /// <summary>Canonical content parts in transcript sequence order.</summary>
    public required IReadOnlyList<ContentPart> Parts { get; init; }

    /// <summary>Native session identifier extracted from transcript metadata.</summary>
    public string? SessionId { get; init; }

    /// <summary>Reported model context window size in tokens.</summary>
    public long? ContextWindow { get; init; }

    /// <summary>Configured/declared model context window in tokens (stored separately from reported windows).</summary>
    public long? DeclaredContextWindow { get; init; }

    /// <summary>Turn termination or exit indicator reported in transcript.</summary>
    public string? TerminationReason { get; init; }

    /// <summary>Process or command exit code reported in transcript.</summary>
    public int? ExitCode { get; init; }

    /// <summary>True when an explicit user correction was detected on this turn.</summary>
    public bool? IsCorrection { get; init; }

    /// <summary>Rule identifier that triggered <see cref="IsCorrection"/>.</summary>
    public string? CorrectionRule { get; init; }

    /// <summary>Native turn/message identifier used to pair transcript turns with provider calls.</summary>
    public string? NativeRecordId { get; init; }

    /// <summary>ISO-8601 UTC timestamp of the turn, when available.</summary>
    public string? Timestamp { get; init; }

    /// <summary>Tool calls invoked during this turn.</summary>
    public IReadOnlyList<ReaderToolCall>? ToolCalls { get; init; }

    /// <summary>Tool results received during this turn.</summary>
    public IReadOnlyList<ReaderToolResult>? ToolResults { get; init; }

    /// <summary>Explicitly offered tool names observed on the turn.</summary>
    public IReadOnlyList<string>? ToolsOffered { get; init; }
}

/// <summary>Classified source envelope pairing a <see cref="ParsedProviderCall"/> with refresh provenance and content.</summary>
public record SourceRecordEnvelope
{
    /// <summary>Classified canonical harness identifier for this source.</summary>
    public required string HarnessId { get; init; }

    /// <summary>Stable unit source key within the harness (for example file path or transcript key).</summary>
    public required string SourceKey { get; init; }

    /// <summary>Native session identifier owned by the harness.</summary>
    public required string NativeSessionId { get; init; }

    /// <summary>Optional explicit native record identifier.</summary>
    public string? NativeRecordId { get; init; }

    /// <summary>Source revision token (for example mtime/size fingerprint or commit hash).</summary>
    public string? SourceRevision { get; init; }

    /// <summary>Parser format identifier (for example <c>"claude-jsonl"</c>, <c>"codex-jsonl"</c>).</summary>
    public string? Format { get; init; }

    /// <summary>Parser implementation identifier.</summary>
    public string? ParserId { get; init; }

    /// <summary>Parser contract version; bumping invalidates source checkpoints cleanly.</summary>
    public string? ParserContractVersion { get; init; }

    /// <summary>ISO-8601 UTC import timestamp.</summary>
    public string? ImportedAt { get; init; }

    /// <summary>Optional byte/line offset token inside the native unit.</summary>
    public string? LocationToken { get; init; }

    /// <summary>Parsed model invocation metrics and identity.</summary>
    public required ParsedProviderCall Call { get; init; }

    /// <summary>Optional paired transcript content and child tool invocations.</summary>
    public ReaderTurn? ReaderTurn { get; init; }
}

/// <summary>
/// Fallback session-file synthesizer producing <c>synth:</c>-namespaced <c>llm.invoke</c>
/// and child <c>tool.invoke</c> <see cref="CanonicalRecord"/> rows from parsed provider calls
/// and transcript reader turns (<c>dash/src/synth/synth.ts</c> and <c>dash/src/canon/measurability.ts</c>).
/// </summary>
public static class CanonSynth
{
    /// <summary>Span and trace identifier prefix reserved for file-synthesized records.</summary>
    public const string SynthSpanPrefix = "synth:";

    /// <summary>Source prefix written onto file-synthesized canonical records.</summary>
    public const string FileSourcePrefix = "codeburn/";

    /// <summary>Default token-convention mapping applied at the parsed-call boundary.</summary>
    public const string DefaultConvention = "exclusive";

    /// <summary>Maximum UTF-8 bytes persisted for synthesized tool result parts (64 KiB).</summary>
    public const int MaxToolResultBytes = 65_536;

    /// <summary>Maximum UTF-8 bytes persisted for synthesized tool arguments JSON (64 KiB).</summary>
    public const int MaxToolArgumentsBytes = 65_536;

    /// <summary>Maximum characters preserved when stamping <c>kyber.session.label</c>.</summary>
    public const int SessionLabelMaxChars = 200;

    /// <summary>Canonical raw key storing invoked skill names.</summary>
    public const string SkillsInvokedKey = "kyber.skills.invoked";

    /// <summary>Canonical raw key storing the session's skill listing.</summary>
    public const string SkillsListedKey = "kyber.skills.listed";

    /// <summary>Canonical raw key storing the user-visible session label.</summary>
    public const string SessionLabelKey = "kyber.session.label";

    /// <summary>Maximum timestamp skew (ms) for twin turn collapsing.</summary>
    public const long TwinTurnMaxSkewMs = 60_000;

    private const string CoppaShutdownReason =
        "Output tokens are excluded from Copilot shutdown rollups to avoid double-counting per-turn requests.";

    private const string CopilotStoreReason =
        "Non-compaction Copilot store rows omit output because per-turn assistant messages already own it, so output is absent rather than a measured zero.";

    private const string CopilotCliToolDefinitionsReason =
        "Copilot CLI session journals carry no tool schemas; the system prompt's <tools> section is usage guidance, not the offered tool set.";

    private const string AntigravityToolDefinitionsReason =
        "Antigravity does not export tool definitions for this turn; its MCP catalogue is paired only where the turn carries it.";

    private static readonly JsonSerializerOptions RawSerializerOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Harnesses/identities excluded from persisted canonical harness storage.</summary>
    public static IReadOnlySet<string> ExcludedHarnessIds { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "gemini",
        "vercel-gateway",
    };

    /// <summary>Explicit input-token convention per upstream provider (41 surveyed providers).</summary>
    public static IReadOnlyDictionary<string, string> ProviderConventions { get; } = BuildProviderConventions();

    private static readonly string[] FileSourceUnmeasurableKeys =
    [
        "schema_cost",
        .. CanonValidation.CanonicalContentKeys,
    ];

    private static readonly Dictionary<string, string[]> ReaderUnmeasurableMap = new(StringComparer.Ordinal)
    {
        ["claude"] = ["schema_cost", "system_prompt", "tool_definitions"],
        ["claude-code"] = ["schema_cost", "system_prompt", "tool_definitions"],
        ["codex"] = ["schema_cost", "tool_definitions"],
        ["kilo"] = ["schema_cost", "system_prompt", "tool_definitions", "instruction_context"],
        ["kilo-code"] = ["schema_cost", "system_prompt", "tool_definitions", "instruction_context"],
        ["kilo-shared-runtime"] = ["schema_cost", "system_prompt", "tool_definitions", "instruction_context"],
        ["kilo-vscode-legacy"] = ["schema_cost", .. CanonValidation.CanonicalContentKeys],
        ["copilot"] = ["schema_cost", .. CanonValidation.CanonicalContentKeys],
        ["copilot-vscode"] = ["schema_cost", "system_prompt", "tool_definitions", "tool_result_content"],
        ["cursor"] = ["schema_cost", "conversation_history", "system_prompt", "tool_definitions"],
        ["cursor-agent"] = ["schema_cost", "conversation_history", "system_prompt", "tool_definitions"],
        ["pi"] = ["schema_cost", "system_prompt", "instruction_context", "tool_definitions", "tool_result_content"],
        ["commandcode"] = ["schema_cost", "system_prompt", "instruction_context", "tool_definitions"],
        ["copilot-cli"] = ["schema_cost", .. CanonValidation.CanonicalContentKeys],
        ["antigravity"] = ["schema_cost", .. CanonValidation.CanonicalContentKeys],
        ["antigravity-cli"] = ["schema_cost", .. CanonValidation.CanonicalContentKeys],
        ["antigravity-ide"] = ["schema_cost", .. CanonValidation.CanonicalContentKeys],
    };

    private static readonly Dictionary<string, string[]> ProviderUnmeasurableMap = new(StringComparer.Ordinal)
    {
        ["gemini"] = ["cache_creation"],
        ["antigravity"] = ["cache_creation"],
        ["antigravity-cli"] = ["cache_creation"],
        ["antigravity-ide"] = ["cache_creation"],
    };

    private static readonly HashSet<string> PerRecordLiftHarnesses = new(StringComparer.Ordinal)
    {
        "kilo-shared-runtime",
        "copilot-cli",
        "antigravity",
        "antigravity-cli",
        "antigravity-ide",
    };

    private static readonly Dictionary<string, HashSet<string>> NeverLiftedByHarness = new(StringComparer.Ordinal)
    {
        ["copilot-cli"] = new HashSet<string>(StringComparer.Ordinal) { "tool_definitions" },
    };

    /// <summary>Returns true when <paramref name="harness"/> is excluded from persisted canonical harness storage.</summary>
    public static bool IsExcludedHarness(string? harness)
    {
        if (string.IsNullOrWhiteSpace(harness))
            return false;

        string trimmed = harness.Trim();
        if (ExcludedHarnessIds.Contains(trimmed))
            return true;

        int slash = trimmed.LastIndexOf('/');
        return slash >= 0 && slash + 1 < trimmed.Length && ExcludedHarnessIds.Contains(trimmed[(slash + 1)..]);
    }

    /// <summary>Returns the canonical token convention (<c>"exclusive"</c> or <c>"inclusive"</c>) for <paramref name="provider"/>.</summary>
    public static string ConventionFor(string provider, IReadOnlyDictionary<string, string>? conventions = null)
    {
        ArgumentNullException.ThrowIfNull(provider);
        IReadOnlyDictionary<string, string> lookup = conventions ?? ProviderConventions;
        return lookup.TryGetValue(provider, out string? convention) ? convention : DefaultConvention;
    }

    /// <summary>Returns the harness identifier for <paramref name="call"/> and optional <paramref name="envelope"/>.</summary>
    public static string HarnessIdFor(ParsedProviderCall call, SourceRecordEnvelope? envelope = null)
    {
        ArgumentNullException.ThrowIfNull(call);
        return !string.IsNullOrEmpty(envelope?.HarnessId) ? envelope.HarnessId : call.Provider;
    }

    /// <summary>
    /// Returns the native session identifier for <paramref name="call"/>, or <see langword="null"/>
    /// when neither the envelope nor the call carries one. Absence stays absent: a fabricated
    /// placeholder would make distinct anonymous calls share one session identity.
    /// </summary>
    public static string? NativeSessionIdFor(ParsedProviderCall call, SourceRecordEnvelope? envelope = null)
    {
        ArgumentNullException.ThrowIfNull(call);
        if (!string.IsNullOrEmpty(envelope?.NativeSessionId))
            return envelope.NativeSessionId;

        return string.IsNullOrEmpty(call.SessionId) ? null : call.SessionId;
    }

    /// <summary>
    /// Derives the stable native record identity and optional SHA-256 fallback digest for one synthesized call.
    /// Cursor pairing identifiers (<see cref="ParsedProviderCall.TurnId"/> when provider is <c>"cursor"</c>)
    /// are excluded from record identity so repeated refreshes keep the original span identity.
    /// </summary>
    public static (string NativeRecordId, string? RecordDigest) NativeRecordIdentity(
        ParsedProviderCall call,
        SourceRecordEnvelope? envelope = null,
        int ordinal = 0)
    {
        ArgumentNullException.ThrowIfNull(call);
        string? cursorPairingId = string.Equals(call.Provider, "cursor", StringComparison.Ordinal) ? call.TurnId : null;

        if (!string.IsNullOrEmpty(envelope?.NativeRecordId)
            && !string.Equals(envelope.NativeRecordId, cursorPairingId, StringComparison.Ordinal))
        {
            return (envelope.NativeRecordId, null);
        }

        if (!string.IsNullOrEmpty(call.TurnId) && cursorPairingId is null)
            return (call.TurnId, null);

        if (!string.IsNullOrEmpty(call.RecordIdentity))
            return (call.RecordIdentity, null);

        string prefix = $"{call.Provider}:{call.SessionId}:";
        if (call.DeduplicationKey.StartsWith(prefix, StringComparison.Ordinal)
            && call.DeduplicationKey.Length > prefix.Length)
        {
            return (call.DeduplicationKey[prefix.Length..], null);
        }

        string payload = string.Create(
            CultureInfo.InvariantCulture,
            $"{NativeSessionIdFor(call, envelope)}\0{call.Timestamp}\0llm.invoke\0{ordinal}");
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(payload));
        string digest = Convert.ToHexStringLower(hash)[..16];
        return (digest, digest);
    }

    /// <summary>Builds <see cref="SourceRecordProvenance"/> for <paramref name="call"/>.</summary>
    public static SourceRecordProvenance ProvenanceFor(
        ParsedProviderCall call,
        SourceRecordEnvelope? envelope = null,
        int ordinal = 0)
    {
        ArgumentNullException.ThrowIfNull(call);
        string harnessId = HarnessIdFor(call, envelope);
        string? nativeSessionId = NativeSessionIdFor(call, envelope);
        (string nativeRecordId, string? recordDigest) = NativeRecordIdentity(call, envelope, ordinal);
        return new SourceRecordProvenance(
            HarnessId: harnessId,
            SourceKey: !string.IsNullOrEmpty(envelope?.SourceKey)
                ? envelope.SourceKey
                : $"{harnessId}:{nativeSessionId ?? string.Empty}",
            NativeSessionId: nativeSessionId,
            NativeRecordId: nativeRecordId,
            RecordDigest: recordDigest,
            SourceRevision: envelope?.SourceRevision,
            ParserContractVersion: envelope?.ParserContractVersion,
            ImportedAt: envelope?.ImportedAt,
            LocationToken: envelope?.LocationToken,
            Format: envelope?.Format,
            ParserId: envelope?.ParserId);
    }

    /// <summary>Computes per-(harness, session, timestamp) occurrence ordinals independent of window slicing.</summary>
    public static int[] WindowIndependentOrdinals(
        IReadOnlyList<ParsedProviderCall> calls,
        IReadOnlyList<SourceRecordEnvelope?>? envelopes = null,
        IDictionary<string, int>? seen = null)
    {
        ArgumentNullException.ThrowIfNull(calls);
        Dictionary<string, int> localSeen = seen as Dictionary<string, int> ?? new Dictionary<string, int>(StringComparer.Ordinal);
        int[] ordinals = new int[calls.Count];

        for (int i = 0; i < calls.Count; i++)
        {
            ParsedProviderCall call = calls[i];
            SourceRecordEnvelope? envelope = envelopes is not null && i < envelopes.Count ? envelopes[i] : null;
            string harnessId = HarnessIdFor(call, envelope);
            if (IsExcludedHarness(harnessId) || IsExcludedHarness(call.Provider))
            {
                ordinals[i] = 0;
                continue;
            }

            string key = $"{harnessId}\0{NativeSessionIdFor(call, envelope)}\0{call.Timestamp}";
            int current = seen is not null
                ? (seen.TryGetValue(key, out int existing) ? existing : 0)
                : localSeen.GetValueOrDefault(key, 0);
            ordinals[i] = current;
            if (seen is not null)
                seen[key] = current + 1;
            else
                localSeen[key] = current + 1;
        }

        return ordinals;
    }

    /// <summary>Returns <c>synth:&lt;harnessId&gt;:&lt;nativeSessionId&gt;</c> trace identifier.</summary>
    public static string TraceIdFor(ParsedProviderCall call, SourceRecordEnvelope? envelope = null)
    {
        ArgumentNullException.ThrowIfNull(call);
        string harnessId = HarnessIdFor(call, envelope);
        string? nativeSessionId = NativeSessionIdFor(call, envelope);
        if (!string.IsNullOrEmpty(nativeSessionId))
            return $"{SynthSpanPrefix}{harnessId}:{nativeSessionId}";

        // With no session identity there is nothing to group by, so key the trace on the record:
        // sharing one empty-session trace would merge unrelated anonymous calls.
        (string nativeRecordId, _) = NativeRecordIdentity(call, envelope);
        return $"{SynthSpanPrefix}{harnessId}:~{nativeRecordId}";
    }

    /// <summary>Returns <c>synth:&lt;harnessId&gt;:&lt;nativeSessionId&gt;:&lt;nativeRecordId&gt;</c> span identifier.</summary>
    public static string SpanIdFor(ParsedProviderCall call, SourceRecordEnvelope? envelope = null, int ordinal = 0)
    {
        ArgumentNullException.ThrowIfNull(call);
        SourceRecordProvenance provenance = ProvenanceFor(call, envelope, ordinal);
        return $"{SynthSpanPrefix}{provenance.HarnessId}:{provenance.NativeSessionId}:{provenance.NativeRecordId}";
    }

    /// <summary>Returns <c>codeburn/&lt;harnessId&gt;</c> telemetry source string.</summary>
    public static string SourceFor(ParsedProviderCall call, SourceRecordEnvelope? envelope = null)
    {
        ArgumentNullException.ThrowIfNull(call);
        return $"{FileSourcePrefix}{HarnessIdFor(call, envelope)}";
    }

    /// <summary>Converts upstream cost figures into a provenance-tagged <see cref="CostBlock"/>.</summary>
    public static CostBlock CostBlockFor(ParsedProviderCall call)
    {
        ArgumentNullException.ThrowIfNull(call);
        if (call.CostUsd != 0 && double.IsFinite(call.CostUsd))
        {
            CostBasis basis = call.CostIsCalculated || (string.Equals(call.Provider, "copilot", StringComparison.Ordinal) && !call.CostHarnessReported)
                ? CostBasis.Published
                : CostBasis.Harness;
            Dictionary<string, double> byModel = new(StringComparer.Ordinal)
            {
                [call.Model] = call.CostUsd,
            };
            return new CostBlock(basis, CostStatus.Priced, call.CostUsd, "USD", byModel);
        }

        return call.CostIsCalculated
            ? new CostBlock(CostBasis.Published, CostStatus.NoRate)
            : new CostBlock(CostBasis.Unknown, CostStatus.NoRate);
    }

    /// <summary>Returns true when <paramref name="call"/> is a Copilot shutdown rollup leg whose output is excluded by design.</summary>
    public static bool IsCopilotShutdownRollup(ParsedProviderCall call)
    {
        ArgumentNullException.ThrowIfNull(call);
        return string.Equals(call.Provider, "copilot", StringComparison.Ordinal)
            && call.OutputTokens == 0
            && call.DeduplicationKey.Contains(":shutdown:", StringComparison.Ordinal);
    }

    /// <summary>Returns the unmeasurable-output reason when Copilot intentionally omitted output tokens, else <see langword="null"/>.</summary>
    public static string? CopilotAbsentOutputReason(ParsedProviderCall call)
    {
        ArgumentNullException.ThrowIfNull(call);
        if (IsCopilotShutdownRollup(call))
            return CoppaShutdownReason;

        bool storeOmitted = string.Equals(call.Provider, "copilot", StringComparison.Ordinal)
            && call.OutputTokens == 0
            && call.DeduplicationKey.StartsWith("copilot-store:", StringComparison.Ordinal)
            && !string.Equals(call.Initiator, "compaction", StringComparison.Ordinal);
        return storeOmitted ? CopilotStoreReason : null;
    }

    /// <summary>Folds exclusive reasoning tokens into <see cref="TokenUsage.Output"/> when the provider bills them on top of output.</summary>
    public static TokenUsage FoldExclusiveReasoning(TokenUsage tokens, string provider)
    {
        ArgumentNullException.ThrowIfNull(tokens);
        ArgumentNullException.ThrowIfNull(provider);
        if (tokens.Reasoning is null or 0)
            return tokens;

        long billed = CanonPricing.BillableOutputTokens(provider, tokens.Output, tokens.Reasoning.Value);
        if (billed == tokens.Output)
            return tokens;

        return tokens with { Output = billed, ReportedOutput = billed };
    }

    /// <summary>Computes the file-sourced measurability declaration map for <paramref name="provider"/>.</summary>
    public static IReadOnlyDictionary<string, JsonElement> MeasurabilityFor(
        string provider,
        IReadOnlyDictionary<string, string[]>? providerUnmeasurable = null)
    {
        ArgumentNullException.ThrowIfNull(provider);
        string family = CanonPricing.SurveyFamily(provider);
        HashSet<string> metrics = new(StringComparer.Ordinal);

        if (ReaderUnmeasurableMap.TryGetValue(provider, out string[]? byProvider))
            metrics.UnionWith(byProvider);
        else if (ReaderUnmeasurableMap.TryGetValue(family, out string[]? byFamily))
            metrics.UnionWith(byFamily);
        else
            metrics.UnionWith(FileSourceUnmeasurableKeys);

        IReadOnlyDictionary<string, string[]> extra = providerUnmeasurable ?? ProviderUnmeasurableMap;
        if (extra.TryGetValue(provider, out string[]? extraProvider))
            metrics.UnionWith(extraProvider);
        else if (extra.TryGetValue(family, out string[]? extraFamily))
            metrics.UnionWith(extraFamily);

        Dictionary<string, JsonElement> map = new(StringComparer.Ordinal);
        foreach (string metric in metrics.Order(StringComparer.Ordinal))
        {
            map[metric] = NotMeasurableElement(ReasonForMetric(provider, family, metric));
        }

        return map;
    }

    /// <summary>Computes per-record measurability applying single-turn lifting for supported readers.</summary>
    public static Dictionary<string, JsonElement> MeasurabilityForRecord(
        string provider,
        string harnessId,
        IReadOnlySet<string> carriedKeys)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(harnessId);
        ArgumentNullException.ThrowIfNull(carriedKeys);

        string harness = CanonPricing.NormalizeHarnessName(harnessId);
        string family = string.Equals(harness, "copilot-cli", StringComparison.Ordinal)
            || string.Equals(harness, "kilo-vscode-legacy", StringComparison.Ordinal)
            ? harness
            : provider;
        IReadOnlyDictionary<string, JsonElement> declared = MeasurabilityFor(family);
        Dictionary<string, JsonElement> stamped = new(declared, StringComparer.Ordinal);
        if (!PerRecordLiftHarnesses.Contains(harness))
            return stamped;

        NeverLiftedByHarness.TryGetValue(harness, out HashSet<string>? neverLifted);
        foreach (string key in carriedKeys)
        {
            if (stamped.ContainsKey(key) && (neverLifted is null || !neverLifted.Contains(key)))
                stamped[key] = MeasuredElement();
        }

        return stamped;
    }

    /// <summary>Computes measurability for a synthesized <c>tool.invoke</c> child span.</summary>
    public static Dictionary<string, JsonElement> MeasurabilityForChild(
        IReadOnlyDictionary<string, JsonElement>? parentMeasurability,
        string provider,
        string harnessId,
        IReadOnlySet<string> childCarriedKeys)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(harnessId);
        ArgumentNullException.ThrowIfNull(childCarriedKeys);

        Dictionary<string, JsonElement> stamped = parentMeasurability is not null
            ? new Dictionary<string, JsonElement>(parentMeasurability, StringComparer.Ordinal)
            : new Dictionary<string, JsonElement>(StringComparer.Ordinal);

        string harness = CanonPricing.NormalizeHarnessName(harnessId);
        if (!PerRecordLiftHarnesses.Contains(harness))
            return stamped;

        string family = string.Equals(harness, "copilot-cli", StringComparison.Ordinal) ? "copilot-cli" : provider;
        IReadOnlyDictionary<string, JsonElement> declared = MeasurabilityFor(family);
        NeverLiftedByHarness.TryGetValue(harness, out HashSet<string>? neverLifted);
        foreach (KeyValuePair<string, JsonElement> kv in declared)
        {
            if (kv.Value.ValueKind != JsonValueKind.Object)
                continue;

            stamped[kv.Key] = childCarriedKeys.Contains(kv.Key) && (neverLifted is null || !neverLifted.Contains(kv.Key))
                ? MeasuredElement()
                : kv.Value;
        }

        return stamped;
    }

    /// <summary>Synthesizes one <c>llm.invoke</c> <see cref="CanonicalRecord"/> from one <see cref="ParsedProviderCall"/>.</summary>
    public static CanonicalRecord SynthesizeCall(
        ParsedProviderCall call,
        IReadOnlyDictionary<string, string>? conventions = null,
        ReaderTurn? readerTurn = null,
        SourceRecordEnvelope? envelope = null,
        int ordinal = 0)
    {
        ArgumentNullException.ThrowIfNull(call);

        long cacheRead = call.CacheReadInputTokens != 0 ? call.CacheReadInputTokens : call.CachedInputTokens;
        long? reasoning = call.ReasoningTokens != 0 ? call.ReasoningTokens : null;

        TokenUsage converted = string.Equals(ConventionFor(call.Provider, conventions), "inclusive", StringComparison.Ordinal)
            ? CanonAdapters.ConvertInclusiveCounts(call.InputTokens, cacheRead, call.CacheCreationInputTokens, call.OutputTokens, true, reasoning)
            : CanonAdapters.ExclusiveConvention(call.InputTokens, cacheRead, call.CacheCreationInputTokens, call.OutputTokens, reasoning);

        TokenUsage tokens = FoldExclusiveReasoning(converted, call.Provider);
        string harness = HarnessIdFor(call, envelope);
        string? sessionId = NativeSessionIdFor(call, envelope);
        SourceRecordProvenance provenance = ProvenanceFor(call, envelope, ordinal);

        HashSet<string> carriedKeys = new(StringComparer.Ordinal);
        if (readerTurn is not null)
        {
            foreach (ContentPart part in readerTurn.Parts)
                carriedKeys.Add(part.Part);
        }

        Dictionary<string, JsonElement> measurability = MeasurabilityForRecord(call.Provider, harness, carriedKeys);
        string? absentOutputReason = CopilotAbsentOutputReason(call);
        if (absentOutputReason is not null)
            measurability["output"] = NotMeasurableElement(absentOutputReason);

        string status = readerTurn?.ExitCode is not null and not 0
            ? "error"
            : string.Equals(readerTurn?.TerminationReason, "task_complete", StringComparison.Ordinal)
                ? "ok"
                : "unspecified";

        IReadOnlyDictionary<string, string> content = readerTurn is null
            ? new Dictionary<string, string>(StringComparer.Ordinal)
            : CanonValidation.ContentFromParts(readerTurn.Parts);

        Dictionary<string, JsonElement> raw = BuildCallRawMap(call, readerTurn, provenance);

        return new CanonicalRecord
        {
            SpanId = SpanIdFor(call, envelope, ordinal),
            TraceId = TraceIdFor(call, envelope),
            ParentSpanId = null,
            Source = SourceFor(call, envelope),
            Harness = harness,
            SessionId = string.IsNullOrEmpty(sessionId) ? null : sessionId,
            Name = $"{call.Provider}:{call.Model}",
            Op = "llm.invoke",
            Kind = "internal",
            Timestamp = call.Timestamp,
            DurationMs = call.ActiveDurationMs ?? 0L,
            Status = status,
            Tokens = tokens,
            Content = content,
            Parts = readerTurn?.Parts,
            Cost = CostBlockFor(call),
            Measurability = measurability,
            Raw = raw,
        };
    }

    /// <summary>Synthesizes one child <c>tool.invoke</c> <see cref="CanonicalRecord"/> paired with a parent LLM turn.</summary>
    public static CanonicalRecord SynthesizeToolCall(
        CanonicalRecord parentRecord,
        ReaderToolCall toolCall,
        ReaderToolResult? toolResult,
        int index = 0,
        ParsedProviderCall? call = null)
    {
        ArgumentNullException.ThrowIfNull(parentRecord);
        ArgumentNullException.ThrowIfNull(toolCall);

        string status = toolResult is null ? "unset" : (toolResult.IsError ? "error" : "ok");
        int? resultBytes = toolResult is not null ? Encoding.UTF8.GetByteCount(toolResult.Content) : null;

        (JsonElement boundedArgsElement, string rawSerializedArgs, int argsBytes, bool argsTruncated, string? canonicalArgsHash) =
            BoundToolArguments(toolCall);

        (long durationMs, JsonElement durationAvailability) = ResolveToolDuration(toolCall, toolResult);

        List<ContentPart> parts = BuildToolResultParts(toolResult, resultBytes);

        string provider = ResolveToolProvider(parentRecord, call);
        HashSet<string> childCarried = toolResult is not null
            ? new HashSet<string>(StringComparer.Ordinal) { "tool_result_content" }
            : new HashSet<string>(StringComparer.Ordinal);

        Dictionary<string, JsonElement> measurability = MeasurabilityForChild(
            parentRecord.Measurability,
            provider,
            parentRecord.Harness,
            childCarried);
        measurability["duration"] = durationAvailability;

        Dictionary<string, JsonElement> raw = BuildToolRawMap(
            toolCall,
            toolResult,
            status,
            resultBytes,
            boundedArgsElement,
            argsBytes,
            argsTruncated,
            canonicalArgsHash,
            durationMs,
            parts);

        string toolKeyId = !string.IsNullOrEmpty(toolCall.Id) ? toolCall.Id : index.ToString(CultureInfo.InvariantCulture);
        byte[] hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes($"{toolKeyId}:{toolCall.Name}:{rawSerializedArgs}"));
        string toolHash = Convert.ToHexStringLower(hashBytes)[..12];

        return new CanonicalRecord
        {
            SpanId = $"{parentRecord.SpanId}-t{toolHash}",
            TraceId = parentRecord.TraceId,
            ParentSpanId = parentRecord.SpanId,
            Source = parentRecord.Source,
            Harness = parentRecord.Harness,
            SessionId = parentRecord.SessionId,
            Name = toolCall.Name,
            Op = "tool.invoke",
            Kind = "internal",
            Timestamp = toolCall.Timestamp ?? parentRecord.Timestamp,
            DurationMs = durationMs,
            Status = status,
            Tokens = new TokenUsage(0, 0, 0, 0, 0, 0),
            Content = new Dictionary<string, string>(StringComparer.Ordinal),
            Parts = parts,
            Cost = new CostBlock(CostBasis.Unknown, CostStatus.NoRate),
            Measurability = measurability,
            Raw = raw,
        };
    }

    /// <summary>Resolves the measured or derived duration for a tool call plus its measurability element.</summary>
    private static (long DurationMs, JsonElement Availability) ResolveToolDuration(
        ReaderToolCall toolCall,
        ReaderToolResult? toolResult)
    {
        if (toolCall.DurationMs is { } measuredDuration)
            return (Math.Max(0, measuredDuration), MeasuredElement());

        if (toolResult is not null
            && !string.IsNullOrEmpty(toolCall.Timestamp)
            && !string.IsNullOrEmpty(toolResult.Timestamp)
            && DateTimeOffset.TryParse(toolCall.Timestamp, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTimeOffset startTs)
            && DateTimeOffset.TryParse(toolResult.Timestamp, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTimeOffset endTs))
        {
            long diffMs = (long)Math.Round((endTs - startTs).TotalMilliseconds);
            if (diffMs >= 0)
                return (diffMs, JsonSerializer.SerializeToElement("derived"));
        }

        return (0, NotMeasurableElement("Tool duration was not reported in telemetry."));
    }

    /// <summary>Builds the truncated-or-intact content parts for a tool result.</summary>
    private static List<ContentPart> BuildToolResultParts(ReaderToolResult? toolResult, int? resultBytes)
    {
        List<ContentPart> parts = [];
        if (toolResult is null)
            return parts;

        int textBytes = resultBytes ?? Encoding.UTF8.GetByteCount(toolResult.Content);
        if (textBytes > MaxToolResultBytes)
        {
            parts.Add(new ContentPart(
                Part: "tool_result_content",
                Text: CanonValidation.TruncateContentPartUtf8(toolResult.Content, MaxToolResultBytes),
                Order: 0,
                Truncated: true));
        }
        else
        {
            parts.Add(new ContentPart(
                Part: "tool_result_content",
                Text: toolResult.Content,
                Order: 0));
        }

        return parts;
    }

    /// <summary>Builds the raw telemetry map for a synthesized tool call.</summary>
    private static Dictionary<string, JsonElement> BuildToolRawMap(
        ReaderToolCall toolCall,
        ReaderToolResult? toolResult,
        string status,
        int? resultBytes,
        JsonElement boundedArgsElement,
        int argsBytes,
        bool argsTruncated,
        string? canonicalArgsHash,
        long durationMs,
        List<ContentPart> parts)
    {
        Dictionary<string, JsonElement> raw = new(StringComparer.Ordinal)
        {
            ["gen_ai.tool.name"] = JsonSerializer.SerializeToElement(toolCall.Name),
            ["gen_ai.tool.call_id"] = JsonSerializer.SerializeToElement(toolCall.Id),
            ["gen_ai.tool.status"] = JsonSerializer.SerializeToElement(status),
            ["gen_ai.tool.arguments_bytes"] = JsonSerializer.SerializeToElement(argsBytes),
            ["arguments"] = boundedArgsElement,
        };

        if (resultBytes.HasValue)
            raw["gen_ai.tool.result_bytes"] = JsonSerializer.SerializeToElement(resultBytes.Value);

        if (argsTruncated)
        {
            if (canonicalArgsHash is not null)
                raw["gen_ai.tool.arguments_hash"] = JsonSerializer.SerializeToElement(canonicalArgsHash);

            raw["gen_ai.tool.arguments_truncated"] = JsonSerializer.SerializeToElement(true);
        }

        if (toolCall.DurationMs is not null || (durationMs >= 0 && !string.IsNullOrEmpty(toolCall.Timestamp) && !string.IsNullOrEmpty(toolResult?.Timestamp)))
            raw["gen_ai.tool.duration_ms"] = JsonSerializer.SerializeToElement(durationMs);

        if (parts.Count > 0)
            raw["result"] = JsonSerializer.SerializeToElement(parts[0].Text);

        return raw;
    }

    /// <summary>Synthesizes all child <c>tool.invoke</c> spans for one parent model invocation turn.</summary>
    public static IReadOnlyList<CanonicalRecord> SynthesizeToolCalls(
        CanonicalRecord parentRecord,
        ParsedProviderCall call,
        ReaderTurn? readerTurn = null,
        IReadOnlyDictionary<string, ReaderToolResult>? sessionToolResults = null)
    {
        ArgumentNullException.ThrowIfNull(parentRecord);
        ArgumentNullException.ThrowIfNull(call);
        if (readerTurn?.ToolCalls is null || readerTurn.ToolCalls.Count == 0)
            return [];

        IReadOnlyList<ReaderToolResult> turnResults = readerTurn.ToolResults ?? [];
        List<CanonicalRecord> records = new(readerTurn.ToolCalls.Count);

        for (int i = 0; i < readerTurn.ToolCalls.Count; i++)
        {
            ReaderToolCall toolCall = readerTurn.ToolCalls[i];
            ReaderToolResult? matched = null;
            if (sessionToolResults is not null && sessionToolResults.TryGetValue(toolCall.Id, out ReaderToolResult? fromSession))
            {
                matched = fromSession;
            }
            else
            {
                foreach (ReaderToolResult r in turnResults)
                {
                    if (string.Equals(r.ToolCallId, toolCall.Id, StringComparison.Ordinal))
                    {
                        matched = r;
                        break;
                    }
                }
            }

            records.Add(SynthesizeToolCall(parentRecord, toolCall, matched, i, call));
        }

        return records;
    }

    /// <summary>
    /// Serial synthesis over paired calls and transcript turns, collapsing contiguous twin
    /// <c>claude-desktop</c> turns, computing window-independent ordinals, and emitting parent
    /// <c>llm.invoke</c> + child <c>tool.invoke</c> canonical records.
    /// </summary>
    public static IReadOnlyList<CanonicalRecord> Synthesize(
        IReadOnlyList<ParsedProviderCall> parsedCalls,
        IReadOnlyList<ReaderTurn?>? readerTurns = null,
        IReadOnlyDictionary<string, string>? conventions = null)
    {
        ArgumentNullException.ThrowIfNull(parsedCalls);
        List<(ParsedProviderCall Call, ReaderTurn? Turn)> items = new(parsedCalls.Count);
        for (int i = 0; i < parsedCalls.Count; i++)
        {
            ReaderTurn? turn = readerTurns is not null && i < readerTurns.Count ? readerTurns[i] : null;
            items.Add((parsedCalls[i], turn));
        }

        List<(ParsedProviderCall Call, ReaderTurn? Turn)> collapsed = CollapseCallAndTurns(items);
        Dictionary<string, ReaderToolResult> sessionToolResults = IndexToolResults(collapsed.Select(static c => c.Turn));

        ParsedProviderCall[] collapsedCalls = collapsed.Select(static c => c.Call).ToArray();
        int[] ordinals = WindowIndependentOrdinals(collapsedCalls);

        List<CanonicalRecord> output = [];
        for (int i = 0; i < collapsed.Count; i++)
        {
            ParsedProviderCall call = collapsed[i].Call;
            if (IsExcludedHarness(call.Provider))
                continue;

            CanonicalRecord parent = SynthesizeCall(call, conventions, collapsed[i].Turn, envelope: null, ordinals[i]);
            output.Add(parent);
            output.AddRange(SynthesizeToolCalls(parent, call, collapsed[i].Turn, sessionToolResults));
        }

        return output;
    }

    /// <summary>Synthesizes classified <see cref="SourceRecordEnvelope"/> entries into canonical records.</summary>
    public static IReadOnlyList<CanonicalRecord> SynthesizeEnvelopes(
        IReadOnlyList<SourceRecordEnvelope> envelopes,
        IReadOnlyDictionary<string, string>? conventions = null)
    {
        ArgumentNullException.ThrowIfNull(envelopes);
        List<SourceRecordEnvelope> collapsed = CollapseEnvelopeTurns(envelopes);
        Dictionary<string, ReaderToolResult> sessionToolResults = IndexToolResults(collapsed.Select(static e => e.ReaderTurn));

        ParsedProviderCall[] calls = collapsed.Select(static e => e.Call).ToArray();
        int[] ordinals = WindowIndependentOrdinals(calls, collapsed);

        List<CanonicalRecord> output = [];
        for (int i = 0; i < collapsed.Count; i++)
        {
            SourceRecordEnvelope envelope = collapsed[i];
            if (IsExcludedHarness(envelope.HarnessId) || IsExcludedHarness(envelope.Call.Provider))
                continue;

            CanonicalRecord parent = SynthesizeCall(envelope.Call, conventions, envelope.ReaderTurn, envelope, ordinals[i]);
            output.Add(parent);
            output.AddRange(SynthesizeToolCalls(parent, envelope.Call, envelope.ReaderTurn, sessionToolResults));
        }

        return output;
    }

    internal static (JsonElement Bounded, bool Truncated) TruncateObjectArguments(JsonElement args, int maxBytes)
    {
        string raw = args.GetRawText();
        if (Encoding.UTF8.GetByteCount(raw) <= maxBytes)
            return (args.Clone(), false);

        JsonNode? parsedNode;
        try
        {
            parsedNode = JsonNode.Parse(raw);
        }
        catch (JsonException)
        {
            return (JsonSerializer.SerializeToElement(new Dictionary<string, object?>()), true);
        }

        if (parsedNode is null)
            return (JsonSerializer.SerializeToElement(new Dictionary<string, object?>()), true);

        List<(Func<string> Get, Action<string> Set)> leaves = [];
        HashSet<JsonNode> visited = new(ReferenceEqualityComparer.Instance);
        CollectStringLeaves(parsedNode, (_, next) => next, leaves, visited);

        for (int pass = 0; pass <= leaves.Count; pass++)
        {
            int size = Encoding.UTF8.GetByteCount(parsedNode.ToJsonString());
            int overage = size - maxBytes;
            if (overage <= 0)
            {
                using JsonDocument okDoc = JsonDocument.Parse(parsedNode.ToJsonString());
                return (okDoc.RootElement.Clone(), true);
            }

            int longestIndex = -1;
            int longestBytes = 0;
            for (int i = 0; i < leaves.Count; i++)
            {
                int leafBytes = Encoding.UTF8.GetByteCount(leaves[i].Get());
                if (leafBytes > longestBytes)
                {
                    longestBytes = leafBytes;
                    longestIndex = i;
                }
            }

            if (longestIndex < 0 || longestBytes == 0)
                break;

            int targetBytes = Math.Max(0, longestBytes - overage);
            string shortened = CanonValidation.TruncateContentPartUtf8(leaves[longestIndex].Get(), targetBytes);
            leaves[longestIndex].Set(shortened);
        }

        string finalJson = parsedNode.ToJsonString();
        if (Encoding.UTF8.GetByteCount(finalJson) <= maxBytes)
        {
            using JsonDocument finalDoc = JsonDocument.Parse(finalJson);
            return (finalDoc.RootElement.Clone(), true);
        }

        return (JsonSerializer.SerializeToElement(new Dictionary<string, object?>()), true);
    }

    private static void CollectStringLeaves(
        JsonNode node,
        Func<JsonNode?, string, JsonNode?> _,
        List<(Func<string> Get, Action<string> Set)> leaves,
        HashSet<JsonNode> visited)
    {
        if (!visited.Add(node))
            return;

        if (node is JsonObject obj)
        {
            List<string> keys = obj.Select(static kv => kv.Key).ToList();
            foreach (string key in keys)
            {
                JsonNode? child = obj[key];
                if (child is JsonValue val && val.TryGetValue<string>(out string? strVal))
                {
                    string capturedKey = key;
                    string current = strVal;
                    leaves.Add((
                        () => current,
                        next =>
                        {
                            current = next;
                            obj[capturedKey] = JsonValue.Create(next);
                        }
                    ));
                }
                else if (child is not null)
                {
                    CollectStringLeaves(child, _, leaves, visited);
                }
            }
        }
        else if (node is JsonArray arr)
        {
            for (int i = 0; i < arr.Count; i++)
            {
                JsonNode? item = arr[i];
                if (item is JsonValue val && val.TryGetValue<string>(out string? strVal))
                {
                    int capturedIndex = i;
                    string current = strVal;
                    leaves.Add((
                        () => current,
                        next =>
                        {
                            current = next;
                            arr[capturedIndex] = JsonValue.Create(next);
                        }
                    ));
                }
                else if (item is not null)
                {
                    CollectStringLeaves(item, _, leaves, visited);
                }
            }
        }
    }

    private static (JsonElement BoundedElement, string RawSerializedArgs, int ArgsBytes, bool Truncated, string? CanonicalHash)
        BoundToolArguments(ReaderToolCall toolCall)
    {
        if (toolCall.RawArgumentsString is not null)
        {
            int rawBytes = Encoding.UTF8.GetByteCount(toolCall.RawArgumentsString);
            if (rawBytes > MaxToolArgumentsBytes)
            {
                string truncatedStr = CanonValidation.TruncateContentPartUtf8(toolCall.RawArgumentsString, MaxToolArgumentsBytes);
                string canonical = CanonFindings.SerializeToolArgs(toolCall.RawArgumentsString);
                string hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
                return (JsonSerializer.SerializeToElement(truncatedStr), toolCall.RawArgumentsString, rawBytes, true, hash);
            }

            return (JsonSerializer.SerializeToElement(toolCall.RawArgumentsString), toolCall.RawArgumentsString, rawBytes, false, null);
        }

        if (toolCall.Arguments is { } structured)
        {
            string serialized = structured.ValueKind == JsonValueKind.String
                ? structured.GetString() ?? string.Empty
                : structured.GetRawText();
            int bytes = Encoding.UTF8.GetByteCount(serialized);

            if (structured.ValueKind == JsonValueKind.String)
            {
                if (bytes > MaxToolArgumentsBytes)
                {
                    string fullStr = structured.GetString() ?? string.Empty;
                    string truncatedStr = CanonValidation.TruncateContentPartUtf8(fullStr, MaxToolArgumentsBytes);
                    string canonical = CanonFindings.SerializeToolArgs(fullStr);
                    string hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
                    return (JsonSerializer.SerializeToElement(truncatedStr), serialized, bytes, true, hash);
                }

                return (structured.Clone(), serialized, bytes, false, null);
            }

            if (structured.ValueKind == JsonValueKind.Object && bytes > MaxToolArgumentsBytes)
            {
                (JsonElement bounded, _) = TruncateObjectArguments(structured, MaxToolArgumentsBytes);
                string canonical = CanonFindings.SerializeToolArgs(structured);
                string hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
                return (bounded, serialized, bytes, true, hash);
            }

            return (structured.Clone(), serialized, bytes, false, null);
        }

        JsonElement emptyObj = JsonSerializer.SerializeToElement(new Dictionary<string, object?>());
        return (emptyObj, "{}", 2, false, null);
    }

    private static Dictionary<string, JsonElement> BuildCallRawMap(
        ParsedProviderCall call,
        ReaderTurn? readerTurn,
        SourceRecordProvenance provenance)
    {
        Dictionary<string, JsonElement> raw = new(StringComparer.Ordinal)
        {
            ["timestamp"] = JsonSerializer.SerializeToElement(call.Timestamp),
            ["provider"] = JsonSerializer.SerializeToElement(call.Provider),
            ["model"] = JsonSerializer.SerializeToElement(call.Model),
            ["inputTokens"] = JsonSerializer.SerializeToElement(call.InputTokens),
            ["outputTokens"] = JsonSerializer.SerializeToElement(call.OutputTokens),
            ["cacheReadInputTokens"] = JsonSerializer.SerializeToElement(call.CacheReadInputTokens),
            ["cacheCreationInputTokens"] = JsonSerializer.SerializeToElement(call.CacheCreationInputTokens),
            ["cachedInputTokens"] = JsonSerializer.SerializeToElement(call.CachedInputTokens),
            ["reasoningTokens"] = JsonSerializer.SerializeToElement(call.ReasoningTokens),
            ["costUSD"] = JsonSerializer.SerializeToElement(call.CostUsd),
            ["costIsCalculated"] = JsonSerializer.SerializeToElement(call.CostIsCalculated),
            ["deduplicationKey"] = JsonSerializer.SerializeToElement(call.DeduplicationKey),
            ["provenance"] = JsonSerializer.SerializeToElement(provenance, RawSerializerOptions),
        };

        if (!string.IsNullOrEmpty(call.SessionId))
            raw["sessionId"] = JsonSerializer.SerializeToElement(call.SessionId);
        if (!string.IsNullOrEmpty(call.TurnId))
            raw["turnId"] = JsonSerializer.SerializeToElement(call.TurnId);
        if (!string.IsNullOrEmpty(call.RecordIdentity))
            raw["recordIdentity"] = JsonSerializer.SerializeToElement(call.RecordIdentity);
        if (!string.IsNullOrEmpty(call.Initiator))
            raw["initiator"] = JsonSerializer.SerializeToElement(call.Initiator);
        if (call.ActiveDurationMs is { } activeMs)
            raw["activeDurationMs"] = JsonSerializer.SerializeToElement(activeMs);
        if (call.Tools is not null)
            raw["tools"] = JsonSerializer.SerializeToElement(call.Tools);
        if (call.BashCommands is not null)
            raw["bashCommands"] = JsonSerializer.SerializeToElement(call.BashCommands);
        if (call.ToolSequence is not null)
            raw["toolSequence"] = JsonSerializer.SerializeToElement(call.ToolSequence);
        if (call.WebSearchRequests is { } webSearches)
            raw["webSearchRequests"] = JsonSerializer.SerializeToElement(webSearches);

        if (readerTurn is not null)
        {
            if (readerTurn.ContextWindow is { } ctxWindow)
                raw["contextWindow"] = JsonSerializer.SerializeToElement(ctxWindow);
            if (readerTurn.DeclaredContextWindow is { } declaredCtxWindow)
                raw[CanonPricing.DeclaredContextLimitKey] = JsonSerializer.SerializeToElement(declaredCtxWindow);
            if (!string.IsNullOrEmpty(readerTurn.TerminationReason))
                raw["terminationReason"] = JsonSerializer.SerializeToElement(readerTurn.TerminationReason);
            if (readerTurn.ExitCode is { } exitCode)
                raw["exitCode"] = JsonSerializer.SerializeToElement(exitCode);
            if (readerTurn.IsCorrection is { } isCorrection)
            {
                raw["isCorrection"] = JsonSerializer.SerializeToElement(isCorrection);
                if (!string.IsNullOrEmpty(readerTurn.CorrectionRule))
                    raw["correctionRule"] = JsonSerializer.SerializeToElement(readerTurn.CorrectionRule);
            }
        }

        IReadOnlyList<string>? invokedSkills = InvokedSkillsOf(call);
        if (invokedSkills is not null)
            raw[SkillsInvokedKey] = JsonSerializer.SerializeToElement(invokedSkills);

        ListedSkillsPayload? listedSkills = ListedSkillsOf(call);
        if (listedSkills is not null)
            raw[SkillsListedKey] = JsonSerializer.SerializeToElement(listedSkills, RawSerializerOptions);

        string? sessionLabel = FormatSessionLabel(call.SessionTitle);
        if (sessionLabel is not null)
            raw[SessionLabelKey] = JsonSerializer.SerializeToElement(sessionLabel);

        return raw;
    }

    private static IReadOnlyList<string>? InvokedSkillsOf(ParsedProviderCall call)
    {
        if (call.Skills is null)
            return null;

        bool isAntigravity = string.Equals(CanonPricing.SurveyFamily(call.Provider), "antigravity", StringComparison.Ordinal);
        if (!isAntigravity && call.Skills.Count == 0)
            return null;

        return call.Skills.Distinct(StringComparer.Ordinal).ToArray();
    }

    private static ListedSkillsPayload? ListedSkillsOf(ParsedProviderCall call)
    {
        if (call.SkillsListed is null || call.SkillsListed.Skills.Count == 0)
            return null;

        ListedSkillItem[] cleaned = call.SkillsListed.Skills
            .Select(static s => new ListedSkillItem(s.Name, s.Description))
            .ToArray();
        return new ListedSkillsPayload(call.SkillsListed.IsInitial, cleaned);
    }

    private static string? FormatSessionLabel(string? sessionTitle)
    {
        if (string.IsNullOrWhiteSpace(sessionTitle))
            return null;

        string trimmed = sessionTitle.Trim();
        return trimmed.Length > SessionLabelMaxChars ? trimmed[..SessionLabelMaxChars] : trimmed;
    }

    private static Dictionary<string, ReaderToolResult> IndexToolResults(IEnumerable<ReaderTurn?> turns)
    {
        Dictionary<string, ReaderToolResult> map = new(StringComparer.Ordinal);
        foreach (ReaderTurn? turn in turns)
        {
            if (turn?.ToolResults is null)
                continue;

            foreach (ReaderToolResult res in turn.ToolResults)
            {
                if (!string.IsNullOrEmpty(res.ToolCallId))
                    map[res.ToolCallId] = res;
            }
        }

        return map;
    }

    private static List<(ParsedProviderCall Call, ReaderTurn? Turn)> CollapseCallAndTurns(
        List<(ParsedProviderCall Call, ReaderTurn? Turn)> items)
    {
        if (items.Count <= 1)
            return items;

        Dictionary<string, List<int>> byKey = new(StringComparer.Ordinal);
        for (int i = 0; i < items.Count; i++)
        {
            ParsedProviderCall call = items[i].Call;
            if (!IsCandidateCollapse(call, call.Provider))
                continue;

            string key = CollapseKey(CallCounterKey(call), call.TurnId);
            if (!byKey.TryGetValue(key, out List<int>? indices))
            {
                indices = [];
                byKey[key] = indices;
            }

            indices.Add(i);
        }

        HashSet<int> dropped = [];
        Dictionary<int, (ParsedProviderCall Call, ReaderTurn? Turn)> mergedInto = [];

        foreach (List<int> group in byKey.Values)
        {
            if (group.Count <= 1)
                continue;

            group.Sort((a, b) => CompareTimestamps(items[a].Call.Timestamp, items[b].Call.Timestamp));
            List<int> cluster = [];
            foreach (int idx in group)
            {
                if (cluster.Count > 0 && TimestampSkewMs(items[cluster[0]].Call.Timestamp, items[idx].Call.Timestamp) > TwinTurnMaxSkewMs)
                    CloseCallCluster(cluster, items, dropped, mergedInto);

                cluster.Add(idx);
            }

            CloseCallCluster(cluster, items, dropped, mergedInto);
        }

        List<(ParsedProviderCall Call, ReaderTurn? Turn)> result = new(items.Count);
        for (int i = 0; i < items.Count; i++)
        {
            if (dropped.Contains(i))
                continue;

            result.Add(mergedInto.TryGetValue(i, out (ParsedProviderCall Call, ReaderTurn? Turn) merged) ? merged : items[i]);
        }

        return result;
    }

    private static List<SourceRecordEnvelope> CollapseEnvelopeTurns(IReadOnlyList<SourceRecordEnvelope> envelopes)
    {
        if (envelopes.Count <= 1)
            return envelopes.ToList();

        Dictionary<string, List<int>> byKey = new(StringComparer.Ordinal);
        for (int i = 0; i < envelopes.Count; i++)
        {
            SourceRecordEnvelope env = envelopes[i];
            if (!IsCandidateCollapse(env.Call, env.HarnessId))
                continue;

            string key = CollapseKey(CallCounterKey(env.Call), env.NativeRecordId);
            if (!byKey.TryGetValue(key, out List<int>? indices))
            {
                indices = [];
                byKey[key] = indices;
            }

            indices.Add(i);
        }

        HashSet<int> dropped = [];
        Dictionary<int, SourceRecordEnvelope> mergedInto = [];

        foreach (List<int> group in byKey.Values)
        {
            if (group.Count <= 1)
                continue;

            group.Sort((a, b) => CompareTimestamps(envelopes[a].Call.Timestamp, envelopes[b].Call.Timestamp));
            List<int> cluster = [];
            foreach (int idx in group)
            {
                if (cluster.Count > 0 && TimestampSkewMs(envelopes[cluster[0]].Call.Timestamp, envelopes[idx].Call.Timestamp) > TwinTurnMaxSkewMs)
                    CloseEnvelopeCluster(cluster, envelopes, dropped, mergedInto);

                cluster.Add(idx);
            }

            CloseEnvelopeCluster(cluster, envelopes, dropped, mergedInto);
        }

        List<SourceRecordEnvelope> result = new(envelopes.Count);
        for (int i = 0; i < envelopes.Count; i++)
        {
            if (dropped.Contains(i))
                continue;

            result.Add(mergedInto.TryGetValue(i, out SourceRecordEnvelope? merged) ? merged : envelopes[i]);
        }

        return result;
    }

    private static void CloseCallCluster(
        List<int> cluster,
        List<(ParsedProviderCall Call, ReaderTurn? Turn)> items,
        HashSet<int> dropped,
        Dictionary<int, (ParsedProviderCall Call, ReaderTurn? Turn)> mergedInto)
    {
        if (cluster.Count > 1)
        {
            int keeperIdx = cluster[0];
            (ParsedProviderCall Call, ReaderTurn? Turn) current = items[keeperIdx];
            for (int i = 1; i < cluster.Count; i++)
            {
                int donorIdx = cluster[i];
                dropped.Add(donorIdx);
                current = (
                    MergeParsedCalls(current.Call, items[donorIdx].Call),
                    MergeReaderTurns(current.Turn, items[donorIdx].Turn));
            }

            mergedInto[keeperIdx] = current;
        }

        cluster.Clear();
    }

    private static void CloseEnvelopeCluster(
        List<int> cluster,
        IReadOnlyList<SourceRecordEnvelope> envelopes,
        HashSet<int> dropped,
        Dictionary<int, SourceRecordEnvelope> mergedInto)
    {
        if (cluster.Count > 1)
        {
            int keeperIdx = cluster[0];
            SourceRecordEnvelope current = envelopes[keeperIdx];
            for (int i = 1; i < cluster.Count; i++)
            {
                int donorIdx = cluster[i];
                SourceRecordEnvelope donor = envelopes[donorIdx];
                dropped.Add(donorIdx);
                current = current with
                {
                    Call = MergeParsedCalls(current.Call, donor.Call),
                    ReaderTurn = MergeReaderTurns(current.ReaderTurn, donor.ReaderTurn),
                };
            }

            mergedInto[keeperIdx] = current;
        }

        cluster.Clear();
    }

    private static T? MaxPresent<T>(T? left, T? right)
        where T : struct, IComparable<T>
    {
        if (left is null)
            return right;
        if (right is null)
            return left;

        return left.Value.CompareTo(right.Value) >= 0 ? left : right;
    }

    private static ParsedProviderCall MergeParsedCalls(ParsedProviderCall keeper, ParsedProviderCall donor)
    {
        string[] tools = (keeper.Tools ?? []).Concat(donor.Tools ?? []).Distinct(StringComparer.Ordinal).ToArray();
        string[] bash = (keeper.BashCommands ?? []).Concat(donor.BashCommands ?? []).Distinct(StringComparer.Ordinal).ToArray();
        string[] sequence = (keeper.ToolSequence ?? []).Concat(donor.ToolSequence ?? []).ToArray();
        string[] skills = (keeper.Skills ?? []).Concat(donor.Skills ?? []).Distinct(StringComparer.Ordinal).ToArray();

        return keeper with
        {
            Tools = tools,
            BashCommands = bash,
            ToolSequence = sequence.Length > 0 ? sequence : null,
            Skills = skills.Length > 0 ? skills : null,
            SkillsListed = keeper.SkillsListed ?? donor.SkillsListed,
            // An absent counter is not a measured zero: keep it absent unless the other side measured it.
            WebSearchRequests = MaxPresent(keeper.WebSearchRequests, donor.WebSearchRequests),
            ActiveDurationMs = MaxPresent(keeper.ActiveDurationMs, donor.ActiveDurationMs),
        };
    }

    private static ReaderTurn? MergeReaderTurns(ReaderTurn? keeper, ReaderTurn? donor)
    {
        if (keeper is null && donor is null)
            return null;
        if (keeper is null)
            return donor;
        if (donor is null)
            return keeper;

        HashSet<string> seenParts = new(keeper.Parts.Select(static p => $"{p.Part}\0{p.Text}"), StringComparer.Ordinal);
        List<ContentPart> mergedParts = [.. keeper.Parts];
        foreach (ContentPart part in donor.Parts)
        {
            if (seenParts.Add($"{part.Part}\0{part.Text}"))
                mergedParts.Add(part);
        }

        List<ReaderToolCall> mergedCalls = [.. keeper.ToolCalls ?? []];
        HashSet<string> callIds = new(mergedCalls.Select(static c => c.Id), StringComparer.Ordinal);
        foreach (ReaderToolCall dt in donor.ToolCalls ?? [])
        {
            if (callIds.Add(dt.Id))
                mergedCalls.Add(dt);
        }

        List<ReaderToolResult> mergedResults = [.. keeper.ToolResults ?? []];
        HashSet<string> resultIds = new(mergedResults.Select(static r => r.ToolCallId), StringComparer.Ordinal);
        foreach (ReaderToolResult dr in donor.ToolResults ?? [])
        {
            if (resultIds.Add(dr.ToolCallId))
                mergedResults.Add(dr);
        }

        string[] mergedOffered = (keeper.ToolsOffered ?? []).Concat(donor.ToolsOffered ?? []).Distinct(StringComparer.Ordinal).ToArray();

        return keeper with
        {
            Parts = mergedParts,
            NativeRecordId = keeper.NativeRecordId ?? donor.NativeRecordId,
            ToolCalls = mergedCalls.Count > 0 ? mergedCalls : null,
            ToolResults = mergedResults.Count > 0 ? mergedResults : null,
            ToolsOffered = mergedOffered.Length > 0 ? mergedOffered : null,
            TerminationReason = keeper.TerminationReason ?? donor.TerminationReason,
            ExitCode = keeper.ExitCode ?? donor.ExitCode,
            IsCorrection = keeper.IsCorrection ?? donor.IsCorrection,
            CorrectionRule = keeper.CorrectionRule ?? donor.CorrectionRule,
        };
    }

    private static bool IsCandidateCollapse(ParsedProviderCall call, string? harnessId)
    {
        if (call.InputTokens == 0
            && call.OutputTokens == 0
            && call.CacheReadInputTokens == 0
            && call.CacheCreationInputTokens == 0
            && call.CachedInputTokens == 0
            && call.ReasoningTokens == 0)
        {
            return false;
        }

        string harness = harnessId ?? call.Provider;
        return string.Equals(harness, "claude-desktop", StringComparison.Ordinal)
            || string.Equals(harness, "kyberdash/claude-desktop", StringComparison.Ordinal)
            || string.Equals(call.Provider, "claude-desktop", StringComparison.Ordinal);
    }

    private static string CallCounterKey(ParsedProviderCall call) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"{call.SessionId ?? string.Empty}:{call.Provider}:{call.Model}:{call.InputTokens}:{call.OutputTokens}:{call.CacheReadInputTokens}:{call.CacheCreationInputTokens}:{call.ReasoningTokens}");

    private static string CollapseKey(string counterKey, string? nativeId) =>
        nativeId is null ? counterKey : $"{counterKey}\0{nativeId}";

    private static int CompareTimestamps(string left, string right)
    {
        bool okLeft = DateTimeOffset.TryParse(left, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTimeOffset dtLeft);
        bool okRight = DateTimeOffset.TryParse(right, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTimeOffset dtRight);
        if (okLeft && okRight)
            return dtLeft.CompareTo(dtRight);
        return string.CompareOrdinal(left, right);
    }

    private static long TimestampSkewMs(string left, string right)
    {
        if (!DateTimeOffset.TryParse(left, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTimeOffset dtLeft)
            || !DateTimeOffset.TryParse(right, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTimeOffset dtRight))
        {
            // An unreadable timestamp says nothing about closeness, so it must never be treated as
            // "same instant": that would collapse unrelated turns.
            return long.MaxValue;
        }

        return (long)Math.Abs((dtRight - dtLeft).TotalMilliseconds);
    }

    private static string ResolveToolProvider(CanonicalRecord parentRecord, ParsedProviderCall? call)
    {
        if (!string.IsNullOrEmpty(call?.Provider))
            return call.Provider;

        int colon = parentRecord.Name.IndexOf(':', StringComparison.Ordinal);
        return colon > 0 ? parentRecord.Name[..colon] : parentRecord.Harness;
    }

    private static JsonElement NotMeasurableElement(string reason) =>
        JsonSerializer.SerializeToElement(new { availability = "not_measurable", reason });

    private static JsonElement MeasuredElement() =>
        JsonSerializer.SerializeToElement("measured");

    private static string ReasonForMetric(string provider, string family, string metric)
    {
        if (string.Equals(metric, "cache_creation", StringComparison.Ordinal)
            && (string.Equals(provider, "gemini", StringComparison.Ordinal) || string.Equals(family, "antigravity", StringComparison.Ordinal)))
        {
            return string.Equals(provider, "gemini", StringComparison.Ordinal)
                ? "Gemini session files do not export a cache-creation counter."
                : "Antigravity conversation stores do not export a cache-creation counter.";
        }

        if (string.Equals(metric, "tool_definitions", StringComparison.Ordinal) && string.Equals(family, "antigravity", StringComparison.Ordinal))
            return AntigravityToolDefinitionsReason;
        if (string.Equals(metric, "tool_definitions", StringComparison.Ordinal) && string.Equals(provider, "copilot-cli", StringComparison.Ordinal))
            return CopilotCliToolDefinitionsReason;
        if (string.Equals(metric, "system_prompt", StringComparison.Ordinal) && string.Equals(provider, "copilot-cli", StringComparison.Ordinal))
            return "Copilot CLI session journals carry the system prompt only on turns the journal pairs with a reader turn.";
        if (string.Equals(metric, "system_prompt", StringComparison.Ordinal) && string.Equals(provider, "copilot-vscode", StringComparison.Ordinal))
            return "VS Code Copilot chat-session files do not store the runtime system prompt.";
        if (string.Equals(metric, "tool_definitions", StringComparison.Ordinal) && string.Equals(provider, "copilot-vscode", StringComparison.Ordinal))
            return "VS Code Copilot chat-session files do not store tool-definition schemas.";
        if (string.Equals(metric, "tool_result_content", StringComparison.Ordinal) && string.Equals(provider, "copilot-vscode", StringComparison.Ordinal))
            return "VS Code Copilot chat-session files do not preserve tool-result content.";

        bool isCursor = provider is "cursor" or "cursor-agent" || string.Equals(family, "cursor", StringComparison.Ordinal);
        if (isCursor)
        {
            return metric switch
            {
                "conversation_history" => "Cursor storage does not retain a complete historical conversation prefix.",
                "system_prompt" => "Cursor storage does not record the static system prompt.",
                "tool_definitions" => "Cursor hook telemetry does not export tool definition schemas.",
                _ => $"Session files for {provider} do not include {metric} data.",
            };
        }

        bool isClaude = provider is "claude" or "claude-code" || string.Equals(family, "claude-code", StringComparison.Ordinal);
        if (isClaude)
        {
            return metric switch
            {
                "system_prompt" => "Claude Code session files do not store the runtime system_prompt.",
                "tool_definitions" => "Claude Code session files record tool invocations, not tool_definitions.",
                _ => $"Session files for {provider} do not include {metric} data.",
            };
        }

        if (string.Equals(metric, "tool_definitions", StringComparison.Ordinal) && string.Equals(family, "codex", StringComparison.Ordinal))
            return "Codex session files record tool names, not tool definition schemas.";

        return $"Session files for {provider} do not include {metric} data.";
    }

    private static Dictionary<string, string> BuildProviderConventions()
    {
        string[] providers =
        [
            "antigravity",
            "claude",
            "cline",
            "cline-cli",
            "codebuff",
            "codewhale",
            "codex",
            "commandcode",
            "copilot",
            "copilot-agent",
            "copilot-cli",
            "copilot-vscode",
            "crush",
            "cursor",
            "cursor-agent",
            "devin",
            "droid",
            "dsh",
            "forge",
            "gemini",
            "goose",
            "grok",
            "hermes",
            "ibm-bob",
            "kilo-code",
            "kimi",
            "kimicode",
            "kiro",
            "lingtai-tui",
            "mistral-vibe",
            "mux",
            "omp",
            "open-design",
            "openclaude",
            "openclaw",
            "pi",
            "quickdesk",
            "qwen",
            "roo-code",
            "vercel-gateway",
            "warp",
            "zcode",
            "zed",
            "zerostack",
        ];

        Dictionary<string, string> map = new(providers.Length, StringComparer.Ordinal);
        foreach (string p in providers)
            map[p] = DefaultConvention;

        return map;
    }
}
