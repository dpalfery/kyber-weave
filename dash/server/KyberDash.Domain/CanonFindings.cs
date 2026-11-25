using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace KyberDash.Domain;

/// <summary>Error signal observed in span status, raw attributes, or tool output.</summary>
public record ErrorSignal(
    string Source,
    string Message,
    string? Code = null,
    string? Timestamp = null);

/// <summary>Detected explicit user-correction turn matching a deterministic rule.</summary>
public record CorrectionTurn(
    int? TurnIndex,
    string? Timestamp,
    string Rule,
    string? Excerpt = null);

/// <summary>Parsed test-suite counts extracted from tool output.</summary>
public record TestSuiteCounts(long Passed, long Failed, long Total);

/// <summary>Change in test-suite results across a run.</summary>
public record TestSuiteDelta(
    AvailabilityResult Availability,
    TestSuiteCounts? Before = null,
    TestSuiteCounts? After = null,
    long? DeltaPassed = null,
    long? DeltaFailed = null);

/// <summary>Observed termination indicator (exit code and/or stop reason).</summary>
public record TerminationSignal(
    AvailabilityResult Availability,
    string? Reason = null,
    long? ExitCode = null);

/// <summary>Summary of error signals observed across a run.</summary>
public record ErrorSummary(
    long Count,
    IReadOnlyList<ErrorSignal> Signals,
    AvailabilityResult Availability);

/// <summary>Summary of explicit user corrections observed across a run.</summary>
public record CorrectionSummary(
    long Count,
    IReadOnlyList<CorrectionTurn> Turns,
    AvailabilityResult Availability);

/// <summary>Empirical outcome block guarding diagnostic findings (D7/D8).</summary>
public record OutcomeBlock(
    string Status,
    string StatusReason,
    TerminationSignal Termination,
    ErrorSummary Errors,
    CorrectionSummary UserCorrections,
    TestSuiteDelta TestDeltas,
    AvailabilityResult Availability);

/// <summary>Result of testing one user message against deterministic correction rules.</summary>
public record CorrectionDetectionResult(
    bool Matched,
    string? Rule = null,
    string? Excerpt = null);

/// <summary>Population coverage report across a corpus of run outcomes.</summary>
public record OutcomePopulationReport(
    int TotalRuns,
    int MeasurableCount,
    int UnmeasurableCount,
    double PopulationRate);

/// <summary>Deep link anchoring a finding to a telemetry span and turn index (D5).</summary>
public record FindingEvidenceLink(
    string SpanId,
    int TurnIndex,
    string Description);

/// <summary>Lower/upper error bar around an estimated token-waste figure (D5).</summary>
public record FindingErrorBar(long Lower, long Upper);

/// <summary>Diagnostic waste finding satisfying the Decision D5/D6 contract.</summary>
public record Finding(
    string Id,
    string DetectorId,
    string Title,
    string Mechanism,
    IReadOnlyList<FindingEvidenceLink> EvidenceLinks,
    string Confidence,
    long? EstimatedWasteTokens,
    string Recommendation,
    FindingErrorBar? ErrorBar,
    string OutcomeRiskCaveat,
    string? RunId = null,
    string? SessionId = null,
    double RankScore = 0,
    string? MeasurementClass = null,
    string? ConfidenceBasis = null,
    string? WhatWouldRaiseIt = null,
    IReadOnlyDictionary<string, object?>? Payload = null);

/// <summary>Validation result returned by <see cref="CanonFindings.LintRecommendationD8"/>.</summary>
public record D8LintResult(bool Valid, IReadOnlyList<string> Errors);

/// <summary>Direct tool-definition item accepted by <see cref="DormantToolSchemaInput"/>.</summary>
public record ToolDefinitionSupply(string Name, long? Tokens = null, string? Server = null);

/// <summary>Inputs for <see cref="CanonFindings.DetectDormantToolSchema"/>.</summary>
public record DormantToolSchemaInput(
    string? RunId = null,
    string? SessionId = null,
    IReadOnlyList<CanonicalRecord>? Records = null,
    OutcomeBlock? Outcome = null,
    IReadOnlyList<ToolDefinitionSupply>? ToolDefinitions = null,
    int? TurnsCount = null,
    IReadOnlyList<string>? Invocations = null,
    IReadOnlyList<string>? References = null);

/// <summary>Direct tool-call item accepted by <see cref="DuplicateToolCallInput"/>.</summary>
public record ToolCallSupply(
    string Name,
    object? Arguments = null,
    string? ArgsHash = null,
    string? SpanId = null,
    int? TurnIndex = null,
    long? Tokens = null,
    string? SessionId = null);

/// <summary>Inputs for <see cref="CanonFindings.DetectDuplicateToolCall"/>.</summary>
public record DuplicateToolCallInput(
    string? RunId = null,
    string? SessionId = null,
    IReadOnlyList<CanonicalRecord>? Records = null,
    IReadOnlyList<ToolCallSupply>? Calls = null,
    OutcomeBlock? Outcome = null);

/// <summary>Direct tool-result item accepted by <see cref="OversizedToolResultInput"/>.</summary>
public record ToolResultSupply(
    string? ToolName,
    object? Content,
    long? Tokens = null,
    int? Characters = null,
    int? Bytes = null,
    string? SpanId = null,
    int? TurnIndex = null);

/// <summary>Inputs for <see cref="CanonFindings.DetectOversizedToolResult"/>.</summary>
public record OversizedToolResultInput(
    string? RunId = null,
    string? SessionId = null,
    IReadOnlyList<CanonicalRecord>? Records = null,
    IReadOnlyList<ToolResultSupply>? Results = null,
    long TokenThreshold = CanonFindings.DefaultFindingTokenThreshold,
    int CharThreshold = CanonFindings.DefaultFindingCharThreshold,
    int ByteThreshold = CanonFindings.DefaultFindingByteThreshold,
    OutcomeBlock? Outcome = null);

/// <summary>Direct turn-prefix item accepted by <see cref="PrefixCacheBreakInput"/>.</summary>
public record PrefixTurnSupply(
    string? SpanId = null,
    int? TurnIndex = null,
    string? PrefixText = null,
    long? FreshInput = null,
    long? CacheRead = null);

/// <summary>Inputs for <see cref="CanonFindings.DetectPrefixCacheBreak"/>.</summary>
public record PrefixCacheBreakInput(
    string? RunId = null,
    string? SessionId = null,
    IReadOnlyList<CanonicalRecord>? Records = null,
    IReadOnlyList<PrefixTurnSupply>? Turns = null,
    OutcomeBlock? Outcome = null);

/// <summary>Direct turn token count item accepted by <see cref="CompactionHazardInput"/>.</summary>
public record CompactionTurnSupply(
    string? SpanId = null,
    int? TurnIndex = null,
    long? InputTokens = null);

/// <summary>Inputs for <see cref="CanonFindings.DetectCompactionHazard"/>.</summary>
public record CompactionHazardInput(
    string? RunId = null,
    string? SessionId = null,
    IReadOnlyList<CanonicalRecord>? Records = null,
    IReadOnlyList<CompactionTurnSupply>? Turns = null,
    long? ContextLimit = null,
    OutcomeBlock? Outcome = null,
    Func<string, string, string?>? SessionIdentityLookup = null,
    Func<IReadOnlyList<CanonicalRecord>, ContextWindowResult?>? ResolveWindow = null);

/// <summary>Inputs for <see cref="CanonFindings.DetectUnboundedDelegation"/>.</summary>
public record UnboundedDelegationInput(
    string? RunId = null,
    string? SessionId = null,
    IReadOnlyList<CanonicalRecord>? Records = null,
    long? RootTokens = null,
    long? SubagentTokens = null,
    string? RootSpanId = null,
    string? SubagentSpanId = null,
    OutcomeBlock? Outcome = null);

/// <summary>Inputs for <see cref="CanonFindings.DetectInactiveSkillReference"/>.</summary>
public record InactiveSkillReferenceInput(
    string? RunId = null,
    string? SessionId = null,
    IReadOnlyList<CanonicalRecord>? Records = null,
    IReadOnlyList<string>? ReferencedSkills = null,
    IReadOnlyList<string>? ExecutedSkills = null,
    IReadOnlyDictionary<string, long>? SkillTokens = null,
    OutcomeBlock? Outcome = null);

/// <summary>Unified inputs for <see cref="CanonFindings.DetectFindings"/>.</summary>
public record DetectFindingsInput(
    string? RunId = null,
    string? SessionId = null,
    IReadOnlyList<CanonicalRecord>? Records = null,
    OutcomeBlock? Outcome = null,
    long? ContextLimit = null,
    Func<string, string, string?>? SessionIdentityLookup = null,
    Func<IReadOnlyList<CanonicalRecord>, ContextWindowResult?>? ResolveWindow = null,
    DormantToolSchemaInput? DormantToolSchema = null,
    DuplicateToolCallInput? DuplicateToolCall = null,
    OversizedToolResultInput? OversizedToolResult = null,
    PrefixCacheBreakInput? PrefixCacheBreak = null,
    CompactionHazardInput? CompactionHazard = null,
    UnboundedDelegationInput? UnboundedDelegation = null,
    InactiveSkillReferenceInput? InactiveSkillReference = null);

/// <summary>
/// Outcome derivation, whitespace-normalized hashing, skill reading, D6 finding ranking,
/// D8 recommendation lint, and all seven waste detectors mirroring
/// <c>dash/src/canon/outcome.ts</c>, <c>dash/src/analysis/signals.ts</c>,
/// <c>dash/src/canon/skills.ts</c>, and <c>dash/src/analysis/findings.ts</c>.
/// </summary>
public static partial class CanonFindings
{
    /// <summary>Default token budget threshold for <c>oversized-tool-result</c>.</summary>
    public const long DefaultFindingTokenThreshold = 2000L;

    /// <summary>Default character length threshold for <c>oversized-tool-result</c>.</summary>
    public const int DefaultFindingCharThreshold = 8000;

    /// <summary>Default UTF-8 byte length threshold for <c>oversized-tool-result</c>.</summary>
    public const int DefaultFindingByteThreshold = 8000;

    private const string SkillsInvokedKey = "kyber.skills.invoked";
    private const string SkillsListedKey = "kyber.skills.listed";
    private const string CopilotListingKey = "github.copilot.context.skills";
    private const string AntigravityListingKey = "gen_ai.skills";
    private const string OpenCodeListingKey = "opencode.context.skills";
    private const string AntigravityListingPrefix = "Active Skills: ";
    private const string BodyListingMarker = "The following skills are available for use with the Skill tool:";

    /// <summary>Ordered identifiers for all seven supported finding detectors.</summary>
    public static IReadOnlyList<string> DetectorIds { get; } =
    [
        "dormant-tool-schema",
        "duplicate-tool-call",
        "oversized-tool-result",
        "prefix-cache-break",
        "compaction-hazard",
        "unbounded-delegation",
        "inactive-skill-reference",
    ];

    /// <summary>Decision D6 confidence multipliers (<c>deterministic</c> &gt; <c>calibrated_statistical</c> &gt; <c>heuristic</c>).</summary>
    public static IReadOnlyDictionary<string, double> ConfidenceMultipliers { get; } =
        new Dictionary<string, double>(StringComparer.Ordinal)
        {
            ["deterministic"] = 1.0,
            ["calibrated_statistical"] = 0.45,
            ["heuristic"] = 0.10,
        };

    [GeneratedRegex(@"\s+", RegexOptions.None, matchTimeoutMilliseconds: 2000)]
    private static partial Regex WhitespaceRunRegex();

    [GeneratedRegex(@"^(?:no[,.\s]|wait[,.\s]|wrong[,.\s]|stop[,.\s]|hold on[,.\s]|actually[,.\s]|don't\b|dont\b|never mind\b|nevermind\b)", RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 2000)]
    private static partial Regex PrefixNegationRegex();

    [GeneratedRegex(@"\b(?:that(?:'s| is)? (?:wrong|incorrect|broken|not what i (?:asked|meant|wanted))|you (?:made a mistake|forgot|broke|misunderstood)|that didn't work|that did not work|that failed|fix (?:the|this) (?:error|bug|issue|failure)|try again|revert (?:that|this)|undo (?:that|this))\b", RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 2000)]
    private static partial Regex MistakeAssertionRegex();

    [GeneratedRegex(@"(?:Traceback \(most recent call last\):|FATAL ERROR:|UnhandledPromiseRejection|panic: )", RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 2000)]
    private static partial Regex FatalToolExceptionRegex();

    [GeneratedRegex(@"^(?:cancelled|aborted|sigint|user_cancelled|interrupted)$", RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 2000)]
    private static partial Regex AbandonedReasonRegex();

    [GeneratedRegex(@"(?:Tests|Test Files)\s*:\s*(?:(\d+)\s*failed,\s*)?(\d+)\s*passed(?:,\s*(\d+)\s*total)?", RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 2000)]
    private static partial Regex VitestHeaderTestRegex();

    [GeneratedRegex(@"(?:✓|passed:?)\s*(\d+)\s*passed(?:\s*(?:\||,)\s*(?:✗|failed:?)\s*(\d+)\s*failed)?", RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 2000)]
    private static partial Regex VitestIconTestRegex();

    [GeneratedRegex(@"(?:(\d+)\s*failed,?\s*)?(\d+)\s*passed(?:\s*in\s*[\d.]+s)?", RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 2000)]
    private static partial Regex PytestTestRegex();

    [GeneratedRegex(@"(\d+)\s*passed\s*,\s*(\d+)\s*failed", RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 2000)]
    private static partial Regex GenericTestRegex();

    [GeneratedRegex(@"test result:\s*(?:ok|FAILED)\.\s*(\d+)\s*passed;\s*(\d+)\s*failed", RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 2000)]
    private static partial Regex CargoGoTestRegex();

    [GeneratedRegex(@"\b(?:delete|deletion|drop|erase)\b", RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 2000)]
    private static partial Regex D8DeleteWordRegex();

    [GeneratedRegex(@"\b(?:remove completely|remove outright|remove permanently|delete outright|delete this (?:rule|skill|tool|instruction))\b", RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 2000)]
    private static partial Regex D8DeletePhraseRegex();

    [GeneratedRegex(@"\b(?:relocat(?:e|ion|ing)|progressive disclosure|on-demand|defer(?:ral|ring|red)?|cache-position)\b", RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 2000)]
    private static partial Regex D8RelocationRegex();

    [GeneratedRegex(@"<skill>([\s\S]*?)</skill>", RegexOptions.None, matchTimeoutMilliseconds: 2000)]
    private static partial Regex OpenCodeSkillElementRegex();

    [GeneratedRegex(@"<description>[\s\S]*?</description>", RegexOptions.None, matchTimeoutMilliseconds: 2000)]
    private static partial Regex DescriptionElementRegex();

    [GeneratedRegex(@"<id>([\s\S]*?)</id>", RegexOptions.None, matchTimeoutMilliseconds: 2000)]
    private static partial Regex IdElementRegex();

    [GeneratedRegex(@"<(?:available_)?skills>", RegexOptions.None, matchTimeoutMilliseconds: 2000)]
    private static partial Regex SkillsTagOpenRegex();

    [GeneratedRegex(@"<skill\b[^>]*>([\s\S]*?)</skill>", RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 2000)]
    private static partial Regex XmlSkillElementRegex();

    [GeneratedRegex(@"<name\b[^>]*>([\s\S]*?)</name>", RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 2000)]
    private static partial Regex XmlNameElementRegex();

    [GeneratedRegex(@"^- (\S+?):(?:\s+(.*))?$", RegexOptions.None, matchTimeoutMilliseconds: 2000)]
    private static partial Regex BodySkillEntryRegex();

    /// <summary>Trims outer whitespace and collapses whitespace runs into a single ASCII space.</summary>
    public static string NormalizeWhitespace(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return WhitespaceRunRegex().Replace(text.Trim(), " ");
    }

    /// <summary>Computes lowercase SHA-256 hex digest over <see cref="NormalizeWhitespace"/> of <paramref name="text"/>.</summary>
    public static string HashNormalized(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        byte[] bytes = Encoding.UTF8.GetBytes(NormalizeWhitespace(text));
        return Convert.ToHexStringLower(SHA256.HashData(bytes));
    }

    /// <summary>Detects explicit user-correction phrases against deterministic D7 rules.</summary>
    public static CorrectionDetectionResult DetectUserCorrection(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return new CorrectionDetectionResult(false);

        string trimmed = text.Trim();
        string excerpt = trimmed.Length > 120 ? trimmed[..117] + "..." : trimmed;

        if (PrefixNegationRegex().IsMatch(trimmed))
            return new CorrectionDetectionResult(true, "prefix_negation", excerpt);

        if (MistakeAssertionRegex().IsMatch(trimmed))
            return new CorrectionDetectionResult(true, "mistake_assertion", excerpt);

        return new CorrectionDetectionResult(false);
    }

    /// <summary>Derives an empirical <see cref="OutcomeBlock"/> from telemetry records without guessing unmeasured outcomes.</summary>
    public static OutcomeBlock DeriveOutcome(IReadOnlyList<CanonicalRecord> records)
    {
        ArgumentNullException.ThrowIfNull(records);

        if (records.Count == 0)
        {
            return new OutcomeBlock(
                Status: "not_measurable",
                StatusReason: "No records provided to observe run outcome.",
                Termination: new TerminationSignal(NotMeasurable("No records available to measure termination.")),
                Errors: new ErrorSummary(0, [], NotMeasurable("No records available to observe error channels.")),
                UserCorrections: new CorrectionSummary(0, [], NotMeasurable("No conversation history available to detect user corrections.")),
                TestDeltas: new TestSuiteDelta(NotMeasurable("No test execution results observed.")),
                Availability: NotMeasurable("Outcome was not observable for empty record set."));
        }

        long? observedExitCode = null;
        string? observedTerminationReason = null;
        List<ErrorSignal> errorSignals = [];
        List<CorrectionTurn> correctionTurns = [];
        bool hasConversationHistory = false;
        List<TestSuiteCounts> testOutputs = [];
        string? lastStatus = null;

        for (int idx = 0; idx < records.Count; idx++)
        {
            CanonicalRecord record = records[idx];
            lastStatus = record.Status;
            string ts = record.Timestamp;

            if (TryGetRawNumber(record, ["exitCode", "exit_code", "status_code"], out double exitNum) && double.IsFinite(exitNum))
                observedExitCode = (long)exitNum;

            string? rawTerm = RawString(record, ["terminationReason", "termination_reason", "stop_reason", "finish_reason"]);
            if (!string.IsNullOrWhiteSpace(rawTerm))
                observedTerminationReason = rawTerm.Trim();

            string? rawType = RawString(record, ["type", "event"]);
            if (rawType is "task_complete" or "task_failed" or "turn_aborted")
            {
                observedTerminationReason = rawType;
                if (rawType == "task_complete" && !observedExitCode.HasValue)
                    observedExitCode = 0;
            }

            if (record.Status is "error" or "fatal" or "failure")
            {
                errorSignals.Add(new ErrorSignal(
                    Source: record.Source,
                    Message: string.IsNullOrEmpty(record.Name) ? "Span error status" : record.Name,
                    Code: record.Status,
                    Timestamp: ts));
            }

            if (TryGetRawElement(record, ["error", "exception"], out JsonElement rawErr))
            {
                errorSignals.Add(new ErrorSignal(
                    Source: record.Harness,
                    Message: rawErr.ValueKind == JsonValueKind.String ? (rawErr.GetString() ?? string.Empty) : rawErr.GetRawText(),
                    Timestamp: ts));
            }

            if (TryGetRawNumber(record, ["editFailed"], out double editFailed) && editFailed > 0)
            {
                errorSignals.Add(new ErrorSignal(
                    Source: record.Harness,
                    Message: $"{(long)editFailed} patch edit(s) failed",
                    Timestamp: ts));
            }

            if (record.Content.TryGetValue("tool_result_content", out string? toolOutput) &&
                !string.IsNullOrEmpty(toolOutput) &&
                FatalToolExceptionRegex().IsMatch(toolOutput))
            {
                errorSignals.Add(new ErrorSignal(
                    Source: record.Harness,
                    Message: "Unhandled exception or panic observed in tool result",
                    Timestamp: ts));
            }

            if (TryGetRawBool(record, ["isCorrection"], out bool isCorrection) && isCorrection)
            {
                hasConversationHistory = true;
                string corrRule = RawString(record, ["correctionRule"]) ?? "explicit_telemetry_indicator";
                correctionTurns.Add(new CorrectionTurn(idx, ts, corrRule));
            }
            else
            {
                string? userMessage = RawString(record, ["userMessage"]);
                if (!string.IsNullOrWhiteSpace(userMessage))
                {
                    hasConversationHistory = true;
                    CorrectionDetectionResult check = DetectUserCorrection(userMessage);
                    if (check.Matched)
                        correctionTurns.Add(new CorrectionTurn(idx, ts, check.Rule ?? "prefix_negation", check.Excerpt));
                }

                if (record.Parts is { Count: > 0 })
                {
                    foreach (ContentPart part in record.Parts)
                    {
                        if (string.Equals(part.Part, "conversation_history", StringComparison.Ordinal))
                        {
                            hasConversationHistory = true;
                            CorrectionDetectionResult check = DetectUserCorrection(part.Text);
                            if (check.Matched)
                                correctionTurns.Add(new CorrectionTurn(idx, ts, check.Rule ?? "prefix_negation", check.Excerpt));
                        }
                    }
                }
                else if (record.Content.TryGetValue("conversation_history", out string? convText) && !string.IsNullOrEmpty(convText))
                {
                    hasConversationHistory = true;
                    CorrectionDetectionResult check = DetectUserCorrection(convText);
                    if (check.Matched)
                        correctionTurns.Add(new CorrectionTurn(idx, ts, check.Rule ?? "prefix_negation", check.Excerpt));
                }
            }

            if (record.Parts is not null)
            {
                foreach (ContentPart part in record.Parts)
                {
                    if (string.Equals(part.Part, "tool_result_content", StringComparison.Ordinal))
                    {
                        TestSuiteCounts? parsed = ParseTestCounts(part.Text);
                        if (parsed is not null)
                            testOutputs.Add(parsed);
                    }
                }
            }
            else if (record.Content.TryGetValue("tool_result_content", out string? contentResult))
            {
                TestSuiteCounts? parsed = ParseTestCounts(contentResult);
                if (parsed is not null)
                    testOutputs.Add(parsed);
            }
        }

        TestSuiteDelta testDeltas;
        if (testOutputs.Count == 0)
        {
            testDeltas = new TestSuiteDelta(NotMeasurable("No test execution results observed in records or tool results."));
        }
        else if (testOutputs.Count == 1)
        {
            TestSuiteCounts counts = testOutputs[0];
            testDeltas = new TestSuiteDelta(Measured(), After: counts, DeltaPassed: counts.Passed, DeltaFailed: counts.Failed);
        }
        else
        {
            TestSuiteCounts before = testOutputs[0];
            TestSuiteCounts after = testOutputs[^1];
            testDeltas = new TestSuiteDelta(
                Measured(),
                Before: before,
                After: after,
                DeltaPassed: after.Passed - before.Passed,
                DeltaFailed: after.Failed - before.Failed);
        }

        TerminationSignal termination = observedExitCode.HasValue || observedTerminationReason is not null
            ? new TerminationSignal(Measured(), observedTerminationReason, observedExitCode)
            : new TerminationSignal(NotMeasurable("No termination or exit indicator was emitted by the harness."));

        ErrorSummary errors = new(errorSignals.Count, errorSignals, Measured());
        CorrectionSummary userCorrections = hasConversationHistory
            ? new CorrectionSummary(correctionTurns.Count, correctionTurns, Measured())
            : new CorrectionSummary(0, [], NotMeasurable("No conversation history available to detect user corrections."));

        string status;
        string statusReason;
        AvailabilityResult outcomeAvail;

        bool isAbandoned = observedTerminationReason is not null && AbandonedReasonRegex().IsMatch(observedTerminationReason);
        if (isAbandoned)
        {
            status = "abandoned";
            statusReason = "Run was cancelled or aborted before completion.";
            outcomeAvail = Measured();
        }
        else if (observedExitCode.HasValue && observedExitCode.Value != 0)
        {
            status = "failure";
            statusReason = $"Process or command terminated with non-zero exit code {observedExitCode.Value}.";
            outcomeAvail = Measured();
        }
        else if (testDeltas.After is { Failed: > 0 })
        {
            status = "failure";
            statusReason = $"Test suite failed with {testDeltas.After.Failed} failing test(s).";
            outcomeAvail = Measured();
        }
        else if (testDeltas.DeltaFailed > 0)
        {
            status = "failure";
            statusReason = $"Test suite delta introduced {testDeltas.DeltaFailed} new test failure(s).";
            outcomeAvail = Measured();
        }
        else if (errorSignals.Count > 0 &&
                 (lastStatus == "error" || observedTerminationReason is "task_failed" or "error" or "fatal"))
        {
            status = "failure";
            statusReason = $"Run terminated with fatal error: {errorSignals[0].Message}.";
            outcomeAvail = Measured();
        }
        else if (userCorrections.Count > 0 &&
                 (observedExitCode == 0 || observedTerminationReason == "task_complete"))
        {
            status = "inconclusive";
            statusReason = $"Run exited with code 0 but required {userCorrections.Count} user correction turn(s).";
            outcomeAvail = Measured();
        }
        else if (errorSignals.Count > 0 &&
                 (observedExitCode == 0 || observedTerminationReason == "task_complete"))
        {
            status = "inconclusive";
            statusReason = $"Run exited cleanly but recorded {errorSignals.Count} non-fatal error signal(s).";
            outcomeAvail = Measured();
        }
        else if ((observedExitCode == 0 || observedTerminationReason == "task_complete") &&
                 errorSignals.Count == 0 &&
                 userCorrections.Count == 0)
        {
            status = "success";
            statusReason = "Run completed cleanly with zero exit code and zero errors or user corrections.";
            outcomeAvail = Measured();
        }
        else if (testDeltas.After is { Passed: > 0, Failed: 0 } &&
                 errorSignals.Count == 0 &&
                 userCorrections.Count == 0)
        {
            status = "success";
            statusReason = $"Test suite passed with {testDeltas.After.Passed} test(s) and zero errors.";
            outcomeAvail = Measured();
        }
        else
        {
            status = "not_measurable";
            statusReason = "No observable outcome signals (exit code, termination event, test deltas, or fatal errors) were emitted.";
            outcomeAvail = NotMeasurable("Outcome was not observable for this run.");
        }

        return new OutcomeBlock(
            Status: status,
            StatusReason: statusReason,
            Termination: termination,
            Errors: errors,
            UserCorrections: userCorrections,
            TestDeltas: testDeltas,
            Availability: outcomeAvail);
    }

    /// <summary>Computes measured-outcome population coverage across a set of runs.</summary>
    public static OutcomePopulationReport CalculateOutcomePopulationRate(IReadOnlyList<OutcomeBlock> outcomes)
    {
        ArgumentNullException.ThrowIfNull(outcomes);
        if (outcomes.Count == 0)
            return new OutcomePopulationReport(0, 0, 0, 0);

        int measurable = 0;
        foreach (OutcomeBlock outcome in outcomes)
        {
            if (!string.Equals(outcome.Status, "not_measurable", StringComparison.Ordinal) && outcome.Availability.IsMeasured)
                measurable++;
        }

        return new OutcomePopulationReport(
            TotalRuns: outcomes.Count,
            MeasurableCount: measurable,
            UnmeasurableCount: outcomes.Count - measurable,
            PopulationRate: (double)measurable / outcomes.Count);
    }

    /// <summary>Computes Decision D6 outcome risk discount in <c>[0.0, 1.0]</c>.</summary>
    public static double CalculateOutcomeRiskDiscount(OutcomeBlock? outcome, string? detectorId = null)
    {
        if (string.Equals(detectorId, "inactive-skill-reference", StringComparison.Ordinal))
            return 0.40;

        if (outcome is null || string.Equals(outcome.Status, "not_measurable", StringComparison.Ordinal))
            return 0.25;

        return outcome.Status switch
        {
            "success" => 0.15,
            "inconclusive" => 0.35,
            "failure" or "abandoned" => 0.50,
            _ => 0.20,
        };
    }

    /// <summary>Derives a plain-language outcome risk caveat string for a finding (D5).</summary>
    public static string DeriveOutcomeRiskCaveat(
        OutcomeBlock? outcome,
        string? detectorId = null,
        string? contextNote = null)
    {
        if (!string.IsNullOrEmpty(contextNote))
            return contextNote;

        if (string.Equals(detectorId, "inactive-skill-reference", StringComparison.Ordinal))
            return "Skill activation is inferred from Skill tool calls and the invoked key (D16); a listed skill that was never loaded may still provide latent domain guidance.";

        if (outcome is null || string.Equals(outcome.Status, "not_measurable", StringComparison.Ordinal))
            return "Run outcome is not measurable from telemetry; verify changes against regression test suites before deployment.";

        return outcome.Status switch
        {
            "success" => "Run completed cleanly with zero errors; context relocation carries minor risk of altering prompt attention weights.",
            "inconclusive" => $"Run required {outcome.UserCorrections.Count} user correction turn(s); altering prompt context may increase correction frequency if critical constraints are shifted.",
            "failure" or "abandoned" => "Run terminated with failure or error signals; ensure context relocation preserves diagnostic instructions needed for error recovery.",
            _ => "Context modification carries outcome regression risk; benchmark against baseline task suites.",
        };
    }

    /// <summary>Computes the Decision D6 finding rank score rounded to 2 decimal places.</summary>
    public static double ComputeRankScore(long estimatedWasteTokens, string confidence, double outcomeRiskDiscount = 0.2)
    {
        double multiplier = ConfidenceMultipliers.GetValueOrDefault(confidence, 0.10);
        double clampedDiscount = Math.Clamp(outcomeRiskDiscount, 0.0, 0.99);
        double score = estimatedWasteTokens * multiplier * (1.0 - clampedDiscount);
        return Math.Max(0, Math.Round(score * 100.0, MidpointRounding.AwayFromZero) / 100.0);
    }

    /// <summary>Orders findings per Decision D6 and D16 (<c>inactive-skill-reference</c> last on ties).</summary>
    public static IReadOnlyList<Finding> RankFindings(IReadOnlyList<Finding> findings)
    {
        ArgumentNullException.ThrowIfNull(findings);
        List<Finding> sorted = [.. findings];
        sorted.Sort(static (a, b) =>
        {
            if (Math.Abs(b.RankScore - a.RankScore) > 0.001)
                return b.RankScore.CompareTo(a.RankScore);

            bool aSkill = string.Equals(a.DetectorId, "inactive-skill-reference", StringComparison.Ordinal);
            bool bSkill = string.Equals(b.DetectorId, "inactive-skill-reference", StringComparison.Ordinal);
            if (aSkill && !bSkill)
                return 1;
            if (bSkill && !aSkill)
                return -1;

            int confDiff = ConfidenceOrder(b.Confidence) - ConfidenceOrder(a.Confidence);
            if (confDiff != 0)
                return confDiff;

            return (b.EstimatedWasteTokens ?? 0).CompareTo(a.EstimatedWasteTokens ?? 0);
        });

        return sorted;
    }

    /// <summary>Enforces Decision D8 recommendation hygiene: relocation/deferral over outright deletion.</summary>
    public static D8LintResult LintRecommendationD8(string recommendation)
    {
        ArgumentNullException.ThrowIfNull(recommendation);
        List<string> errors = [];

        if (D8DeleteWordRegex().IsMatch(recommendation) || D8DeletePhraseRegex().IsMatch(recommendation))
        {
            errors.Add("Decision D8 violation: recommendation suggests outright deletion. Use relocation, progressive disclosure, or deferral instead.");
        }

        if (!D8RelocationRegex().IsMatch(recommendation))
        {
            errors.Add("Decision D8 violation: recommendation must explicitly suggest relocation, progressive disclosure, on-demand loading, or tool deferral.");
        }

        return new D8LintResult(errors.Count == 0, errors);
    }

    /// <summary>Normalizes structured or string tool arguments for deterministic hashing.</summary>
    public static string SerializeToolArgs(object? args)
    {
        if (args is null)
            return string.Empty;

        if (args is JsonElement el)
        {
            if (el.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
                return string.Empty;
            if (el.ValueKind == JsonValueKind.String)
                return SerializeToolArgs(el.GetString());
            return NormalizeWhitespace(el.GetRawText());
        }

        if (args is string text)
        {
            try
            {
                using JsonDocument parsed = JsonDocument.Parse(text);
                return NormalizeWhitespace(parsed.RootElement.GetRawText());
            }
            catch (JsonException)
            {
                return NormalizeWhitespace(text);
            }
        }

        try
        {
            return NormalizeWhitespace(JsonSerializer.Serialize(args));
        }
        catch (NotSupportedException)
        {
            return args.ToString() ?? string.Empty;
        }
    }

    /// <summary>
    /// Extracts tool definitions (and per-tool token count only when a measured count covers a
    /// single-definition part) matching <c>dash/src/analysis/findings.ts:extractToolDefinitionsFromRecord</c>.
    /// </summary>
    public static IReadOnlyList<(string Name, long? Tokens)> ExtractToolDefinitionsFromRecord(CanonicalRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        List<(string Name, long? Tokens)> tools = [];
        if (record.Parts is null)
            return tools;

        foreach (ContentPart part in record.Parts)
        {
            if (!string.Equals(part.Part, "tool_definitions", StringComparison.Ordinal) || string.IsNullOrEmpty(part.Text))
                continue;

            long? measured = part.Tokens is > 0 ? part.Tokens : null;
            try
            {
                using JsonDocument doc = JsonDocument.Parse(part.Text);
                JsonElement root = doc.RootElement;
                if (root.ValueKind == JsonValueKind.Array)
                {
                    int arrayLen = root.GetArrayLength();
                    List<string> named = [];
                    foreach (JsonElement item in root.EnumerateArray())
                    {
                        if (item.ValueKind == JsonValueKind.Object &&
                            item.TryGetProperty("name", out JsonElement nameEl) &&
                            nameEl.ValueKind == JsonValueKind.String &&
                            !string.IsNullOrEmpty(nameEl.GetString()))
                        {
                            named.Add(nameEl.GetString()!);
                        }
                    }

                    long? assignedTokens = measured.HasValue && arrayLen == 1 && named.Count == 1 ? measured : null;
                    foreach (string name in named)
                        tools.Add((name, assignedTokens));
                }
                else if (root.ValueKind == JsonValueKind.Object &&
                         root.TryGetProperty("name", out JsonElement objName) &&
                         objName.ValueKind == JsonValueKind.String &&
                         !string.IsNullOrEmpty(objName.GetString()))
                {
                    tools.Add((objName.GetString()!, measured));
                }
            }
            catch (JsonException)
            {
                string trimmed = part.Text.Trim();
                if (trimmed.Length > 0)
                    tools.Add((trimmed, measured));
            }
        }

        return tools;
    }

    /// <summary>Detector 1: tool schemas resident across &gt;= 3 turns with 0 invocations and no references.</summary>
    public static IReadOnlyList<Finding> DetectDormantToolSchema(DormantToolSchemaInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        IReadOnlyList<CanonicalRecord> records = input.Records ?? [];
        string? sessionId = !string.IsNullOrEmpty(input.SessionId)
            ? input.SessionId
            : (records.Count > 0 && !string.IsNullOrEmpty(records[0].SessionId) ? records[0].SessionId : null);
        double discount = CalculateOutcomeRiskDiscount(input.Outcome, "dormant-tool-schema");

        List<CanonicalRecord> turnRecords = [];
        List<CanonicalRecord> toolRecords = [];
        foreach (CanonicalRecord record in records)
        {
            if (string.Equals(record.Op, "llm.invoke", StringComparison.Ordinal))
                turnRecords.Add(record);
            else if (string.Equals(record.Op, "tool.invoke", StringComparison.Ordinal))
                toolRecords.Add(record);
        }

        Dictionary<string, ToolResidency> toolResidency = new(StringComparer.Ordinal);
        for (int turnIdx = 0; turnIdx < turnRecords.Count; turnIdx++)
        {
            CanonicalRecord turn = turnRecords[turnIdx];
            foreach ((string toolName, long? tokens) in ExtractToolDefinitionsFromRecord(turn))
            {
                if (!toolResidency.TryGetValue(toolName, out ToolResidency? existing))
                {
                    existing = new ToolResidency();
                    toolResidency[toolName] = existing;
                }

                existing.Turns.Add((turn.SpanId, turnIdx));
                if (tokens is > 0)
                    existing.Tokens = Math.Max(existing.Tokens ?? 0, tokens.Value);
            }
        }

        if (input.ToolDefinitions is { Count: > 0 })
        {
            int totalTurns = input.TurnsCount ?? turnRecords.Count;
            foreach (ToolDefinitionSupply def in input.ToolDefinitions)
            {
                if (toolResidency.TryGetValue(def.Name, out ToolResidency? existing))
                {
                    if (def.Tokens is > 0)
                        existing.Tokens = Math.Max(existing.Tokens ?? 0, def.Tokens.Value);
                }
                else
                {
                    ToolResidency created = new() { Tokens = def.Tokens is > 0 ? def.Tokens : null };
                    for (int i = 0; i < totalTurns; i++)
                    {
                        string spanId = i < turnRecords.Count ? turnRecords[i].SpanId : $"turn-span-{i}";
                        created.Turns.Add((spanId, i));
                    }

                    toolResidency[def.Name] = created;
                }
            }
        }

        HashSet<string> invokedNames = new(StringComparer.Ordinal);
        foreach (CanonicalRecord toolRec in toolRecords)
        {
            string? resolved = RawString(toolRec, ["gen_ai.tool.name", "tool.name", "tool_name"])
                ?? (toolRec.Name != "tool.invoke" ? toolRec.Name : null);
            invokedNames.Add((resolved ?? toolRec.Name).Trim());
        }

        if (input.Invocations is not null)
        {
            foreach (string inv in input.Invocations)
                invokedNames.Add(inv.Trim());
        }

        string conversationText = string.Join(
            " ",
            turnRecords.Select(static t => t.Content.TryGetValue("conversation_history", out string? text) ? text : string.Empty));
        IReadOnlyList<string> extraRefs = input.References ?? [];

        List<Finding> findings = [];
        foreach (KeyValuePair<string, ToolResidency> entry in toolResidency)
        {
            string toolName = entry.Key;
            ToolResidency residency = entry.Value;
            if (residency.Turns.Count < 3 || invokedNames.Contains(toolName))
                continue;

            bool hasRef = conversationText.Contains(toolName, StringComparison.OrdinalIgnoreCase) ||
                          extraRefs.Any(r => r.Contains(toolName, StringComparison.OrdinalIgnoreCase));
            if (hasRef)
                continue;

            int turnsResident = residency.Turns.Count;
            long? schemaTokens = residency.Tokens;
            bool measured = schemaTokens.HasValue;
            long? estimatedWasteTokens = measured ? schemaTokens!.Value * turnsResident : null;

            (string firstSpanId, int firstTurnIdx) = residency.Turns[0];
            (string lastSpanId, int lastTurnIdx) = residency.Turns[^1];

            FindingEvidenceLink[] evidenceLinks =
            [
                new(firstSpanId, firstTurnIdx, measured
                    ? $"Tool schema for \"{toolName}\" first registered in context ({schemaTokens} tokens)."
                    : $"Tool schema for \"{toolName}\" first registered in context (size unmeasured)."),
                new(lastSpanId, lastTurnIdx,
                    $"Turn {lastTurnIdx} executed with tool \"{toolName}\" still resident in prompt context after {turnsResident} turns with 0 invocations."),
            ];

            string recommendation = $"Relocate tool schema \"{toolName}\" to on-demand loading or tool deferral using progressive disclosure, registering definitions only when task phases require them.";
            string outcomeRiskCaveat = DeriveOutcomeRiskCaveat(
                input.Outcome,
                "dormant-tool-schema",
                "Relocating tool schemas to on-demand loading requires the agent to be aware of tool availability; ensure tool discovery prompts are provided.");

            findings.Add(new Finding(
                Id: $"finding-dormant-tool-{toolName}-{sessionId ?? "session"}",
                DetectorId: "dormant-tool-schema",
                Title: $"Dormant Tool Schema: \"{toolName}\" resident across {turnsResident} turns without invocation",
                Mechanism: measured
                    ? $"Tool schema definition for \"{toolName}\" was injected into the prompt context on each turn ({schemaTokens} tokens/turn), accumulating {estimatedWasteTokens} tokens across {turnsResident} turns with zero invocations and no model references."
                    : $"Tool schema definition for \"{toolName}\" was injected into the prompt context on each turn at an unmeasured size, remaining resident across {turnsResident} turns with zero invocations and no model references.",
                EvidenceLinks: evidenceLinks,
                Confidence: "deterministic",
                EstimatedWasteTokens: estimatedWasteTokens,
                Recommendation: recommendation,
                ErrorBar: null,
                OutcomeRiskCaveat: outcomeRiskCaveat,
                RunId: input.RunId,
                SessionId: sessionId,
                RankScore: ComputeRankScore(estimatedWasteTokens ?? 0, "deterministic", discount),
                MeasurementClass: measured ? "deterministic" : "coverage-gap",
                ConfidenceBasis: "Deterministically measured from schema presence in turn context and 0 recorded invocation spans.",
                WhatWouldRaiseIt: measured
                    ? "Telemetry is already deterministic; confidence is at ceiling."
                    : "Reported schema token sizes on the resident tool definitions would let the unused schema cost be measured."));
        }

        return findings;
    }

    /// <summary>Detector 2: identical tool invocations with matching arguments within the same session.</summary>
    public static IReadOnlyList<Finding> DetectDuplicateToolCall(DuplicateToolCallInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        IReadOnlyList<CanonicalRecord> records = input.Records ?? [];
        string? sessionId = !string.IsNullOrEmpty(input.SessionId)
            ? input.SessionId
            : (records.Count > 0 && !string.IsNullOrEmpty(records[0].SessionId) ? records[0].SessionId : null);
        double discount = CalculateOutcomeRiskDiscount(input.Outcome, "duplicate-tool-call");

        List<NormalizedToolCall> calls = [];
        int turnIdx = 0;
        foreach (CanonicalRecord r in records)
        {
            if (string.Equals(r.Op, "llm.invoke", StringComparison.Ordinal))
            {
                turnIdx++;
            }
            else if (string.Equals(r.Op, "tool.invoke", StringComparison.Ordinal))
            {
                string toolName = (!string.IsNullOrEmpty(r.Name) && r.Name != "tool.invoke" ? r.Name : null)
                    ?? RawString(r, ["gen_ai.tool.name", "tool.name", "tool_name"])
                    ?? "tool.invoke";
                object? args = r.Raw is not null && r.Raw.TryGetValue("arguments", out JsonElement argsEl) ? argsEl : null;
                string? argsHash = RawString(r, ["gen_ai.tool.arguments_hash"]);

                // Only reported counters count: a token figure derived from result bytes or text length is an estimate (D15).
                long sumTokens = r.Tokens.ReportedInput + r.Tokens.Output;
                long? tokens = sumTokens > 0 ? sumTokens : null;

                calls.Add(new NormalizedToolCall(
                    toolName.Trim(),
                    SerializeToolArgs(args),
                    argsHash,
                    r.SpanId,
                    turnIdx,
                    tokens,
                    r.SessionId ?? sessionId));
            }
        }

        if (input.Calls is { Count: > 0 })
        {
            for (int i = 0; i < input.Calls.Count; i++)
            {
                ToolCallSupply c = input.Calls[i];
                calls.Add(new NormalizedToolCall(
                    c.Name.Trim(),
                    SerializeToolArgs(c.Arguments),
                    c.ArgsHash,
                    c.SpanId ?? $"tool-call-span-{i}",
                    c.TurnIndex ?? i,
                    c.Tokens,
                    c.SessionId ?? sessionId));
            }
        }

        List<Finding> findings = [];
        Dictionary<string, NormalizedToolCall> seenMap = new(StringComparer.Ordinal);
        foreach (NormalizedToolCall call in calls)
        {
            string hashedArgs = call.ArgsHash ?? Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(call.Args)));
            string callSessionId = call.SessionId ?? sessionId ?? string.Empty;
            string key = $"{callSessionId}::{call.Name}::{hashedArgs}";

            if (seenMap.TryGetValue(key, out NormalizedToolCall? firstCall))
            {
                long? wasteTokens = call.Tokens;
                bool measured = wasteTokens.HasValue;
                string preview = call.Args.Length == 0
                    ? "(empty)"
                    : (call.Args.Length > 100 ? call.Args[..100] : call.Args);

                FindingEvidenceLink[] evidenceLinks =
                [
                    new(firstCall.SpanId, firstCall.TurnIndex, $"Initial invocation of \"{call.Name}\" with arguments: {preview}."),
                    new(call.SpanId, call.TurnIndex, $"Duplicate identical invocation of \"{call.Name}\" with matching arguments, generating redundant output."),
                ];

                string recommendation = $"Implement client-side on-demand caching or relocate query results into local conversation state to defer duplicate \"{call.Name}\" tool executions.";
                string outcomeRiskCaveat = DeriveOutcomeRiskCaveat(
                    input.Outcome,
                    "duplicate-tool-call",
                    "Client-side result caching assumes queried external resources are idempotent; volatile resources may yield stale data if cached.");

                findings.Add(new Finding(
                    Id: $"finding-duplicate-call-{call.Name}-{call.SpanId}",
                    DetectorId: "duplicate-tool-call",
                    Title: $"Duplicate Tool Call: Identical invocation of \"{call.Name}\"",
                    Mechanism: measured
                        ? $"The tool \"{call.Name}\" was executed multiple times with identical arguments in the same session, repeating {wasteTokens} tokens of redundant call and result payloads."
                        : $"The tool \"{call.Name}\" was executed multiple times with identical arguments in the same session, repeating a redundant call and result payload of unmeasured size.",
                    EvidenceLinks: evidenceLinks,
                    Confidence: "deterministic",
                    EstimatedWasteTokens: wasteTokens,
                    Recommendation: recommendation,
                    ErrorBar: null,
                    OutcomeRiskCaveat: outcomeRiskCaveat,
                    RunId: input.RunId,
                    SessionId: call.SessionId ?? sessionId,
                    RankScore: ComputeRankScore(wasteTokens ?? 0, "deterministic", discount),
                    MeasurementClass: measured ? "deterministic" : "coverage-gap",
                    ConfidenceBasis: "Deterministically measured by hashing tool names and whitespace-normalized arguments across execution spans.",
                    WhatWouldRaiseIt: measured
                        ? "Deterministic measurement; confidence is at ceiling."
                        : "Reported token counters or result byte counts on the duplicate tool spans would let the redundant payload be measured."));
            }
            else
            {
                seenMap[key] = call;
            }
        }

        return findings;
    }

    /// <summary>Detector 3: tool outputs exceeding token, byte, or character thresholds.</summary>
    public static IReadOnlyList<Finding> DetectOversizedToolResult(OversizedToolResultInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        IReadOnlyList<CanonicalRecord> records = input.Records ?? [];
        string? sessionId = !string.IsNullOrEmpty(input.SessionId)
            ? input.SessionId
            : (records.Count > 0 && !string.IsNullOrEmpty(records[0].SessionId) ? records[0].SessionId : null);
        double discount = CalculateOutcomeRiskDiscount(input.Outcome, "oversized-tool-result");

        List<ToolResultItem> items = [];
        int currentTurnIndex = 0;
        string lastLlmSpanId = string.Empty;

        foreach (CanonicalRecord r in records)
        {
            if (string.Equals(r.Op, "llm.invoke", StringComparison.Ordinal))
            {
                currentTurnIndex++;
                lastLlmSpanId = r.SpanId;
            }
            else if (string.Equals(r.Op, "tool.invoke", StringComparison.Ordinal))
            {
                StringBuilder contentBuilder = new();
                long reportedTokens = 0;
                bool allPartsMeasured = true;
                if (r.Parts is not null)
                {
                    foreach (ContentPart part in r.Parts)
                    {
                        if (string.Equals(part.Part, "tool_result_content", StringComparison.Ordinal) && !string.IsNullOrEmpty(part.Text))
                        {
                            contentBuilder.Append(part.Text);
                            long? partTokens = CanonPricing.MeasuredCount(part.Tokens);
                            if (partTokens.HasValue)
                                reportedTokens += partTokens.Value;
                            else
                                allPartsMeasured = false;
                        }
                    }
                }

                string content = contentBuilder.ToString();
                if (content.Length == 0 && TryGetRawElement(r, ["result", "output"], out JsonElement rawRes))
                {
                    content = rawRes.ValueKind == JsonValueKind.String ? (rawRes.GetString() ?? string.Empty) : rawRes.GetRawText();
                    allPartsMeasured = false;
                }

                string toolName = (!string.IsNullOrEmpty(r.Name) && r.Name != "tool.invoke" ? r.Name : null)
                    ?? RawString(r, ["gen_ai.tool.name", "tool.name", "tool_name"])
                    ?? "tool";

                long? resultBytes = TryGetRawNumber(r, ["gen_ai.tool.result_bytes"], out double rb) ? (long)rb : null;
                int chars = content.Length;
                long bytes = resultBytes ?? Encoding.UTF8.GetByteCount(content);

                // A token figure derived from bytes or text length is an estimate (D15): without reported tokens the item stays unmeasured.
                long? measuredTokens = allPartsMeasured && reportedTokens > 0 ? reportedTokens : null;

                if (chars > 0 || bytes > 0 || content.Length > 0)
                {
                    items.Add(new ToolResultItem(
                        toolName,
                        measuredTokens,
                        chars,
                        bytes,
                        r.SpanId,
                        currentTurnIndex,
                        string.IsNullOrEmpty(lastLlmSpanId) ? r.SpanId : lastLlmSpanId,
                        r.SessionId ?? sessionId));
                }
            }
        }

        if (input.Results is { Count: > 0 })
        {
            for (int i = 0; i < input.Results.Count; i++)
            {
                ToolResultSupply res = input.Results[i];
                string content = res.Content switch
                {
                    null => string.Empty,
                    string s => s,
                    JsonElement el when el.ValueKind == JsonValueKind.String => el.GetString() ?? string.Empty,
                    JsonElement el => el.GetRawText(),
                    _ => JsonSerializer.Serialize(res.Content),
                };
                int chars = res.Characters ?? content.Length;
                long bytes = res.Bytes ?? Encoding.UTF8.GetByteCount(content);
                items.Add(new ToolResultItem(
                    res.ToolName ?? "tool",
                    res.Tokens,
                    chars,
                    bytes,
                    res.SpanId ?? $"result-span-{i}",
                    res.TurnIndex ?? i,
                    $"llm-consuming-span-{i}",
                    sessionId));
            }
        }

        List<Finding> findings = [];
        foreach (ToolResultItem item in items)
        {
            bool tokenExceeded = item.Tokens > input.TokenThreshold;
            bool charExceeded = item.Chars > input.CharThreshold;
            bool byteExceeded = item.Bytes > input.ByteThreshold;
            if (!tokenExceeded && !charExceeded && !byteExceeded)
                continue;

            // The excess is exact arithmetic on a reported token count; a bytes- or characters-only
            // overrun has no reported token figure, so it is not measured rather than derived at a ratio (D15).
            string titleExceeded;
            string thresholdDesc;
            long? excessTokens;
            if (tokenExceeded)
            {
                titleExceeded = $"{item.Tokens} tokens";
                thresholdDesc = $"{input.TokenThreshold} tokens";
                excessTokens = item.Tokens!.Value - input.TokenThreshold;
            }
            else if (byteExceeded)
            {
                titleExceeded = $"{item.Bytes} bytes";
                thresholdDesc = $"{input.ByteThreshold} bytes";
                excessTokens = null;
            }
            else
            {
                titleExceeded = $"{item.Chars} characters";
                thresholdDesc = $"{input.CharThreshold} characters";
                excessTokens = null;
            }

            bool excessMeasured = excessTokens.HasValue;

            FindingEvidenceLink[] evidenceLinks =
            [
                new(item.SpanId, item.TurnIndex, $"Tool \"{item.ToolName}\" returned oversized output ({item.tokensDisplay}), exceeding threshold ({thresholdDesc})."),
                new(item.ConsumingSpanId, item.TurnIndex + 1, item.Tokens.HasValue
                    ? $"Subsequent turn context re-read {item.Tokens} tokens into prompt history with low operational yield."
                    : "Subsequent turn context re-read the result into prompt history with low operational yield (token size not reported)."),
            ];

            string recommendation = "Relocate large tool outputs to persistent storage and use progressive disclosure or on-demand pagination to return concise excerpts.";
            string outcomeRiskCaveat = DeriveOutcomeRiskCaveat(
                input.Outcome,
                "oversized-tool-result",
                "Truncating or paginating tool output may conceal error traces or multi-line diagnostics if subsequent steps require full logs.");

            findings.Add(new Finding(
                Id: $"finding-oversized-result-{item.ToolName}-{item.SpanId}",
                DetectorId: "oversized-tool-result",
                Title: $"Oversized Tool Result: \"{item.ToolName}\" returned {titleExceeded} exceeding budget",
                Mechanism: excessMeasured
                    ? $"Tool \"{item.ToolName}\" injected {item.Tokens} tokens ({item.Chars} characters, {item.Bytes} bytes) into context in a single output, exceeding the {thresholdDesc} limit and increasing per-turn re-read overhead by {excessTokens} tokens."
                    : $"Tool \"{item.ToolName}\" injected {item.Chars} characters ({item.Bytes} bytes) into context in a single output, exceeding the {thresholdDesc} limit; no reported token count, so the added re-read overhead is not measured.",
                EvidenceLinks: evidenceLinks,
                Confidence: "deterministic",
                EstimatedWasteTokens: excessTokens,
                Recommendation: recommendation,
                ErrorBar: null,
                OutcomeRiskCaveat: outcomeRiskCaveat,
                RunId: input.RunId,
                SessionId: item.SessionId ?? sessionId,
                RankScore: ComputeRankScore(excessTokens ?? 0, "deterministic", discount),
                MeasurementClass: "deterministic",
                ConfidenceBasis: "Deterministically measured by comparing tool result byte, character, and token lengths against configured thresholds.",
                WhatWouldRaiseIt: "Deterministic measurement; confidence is at ceiling."));
        }

        return findings;
    }

    /// <summary>Detector 4: consecutive prompt-prefix hash divergence invalidating KV-cache reuse.</summary>
    public static IReadOnlyList<Finding> DetectPrefixCacheBreak(PrefixCacheBreakInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        IReadOnlyList<CanonicalRecord> records = input.Records ?? [];
        string? sessionId = !string.IsNullOrEmpty(input.SessionId)
            ? input.SessionId
            : (records.Count > 0 && !string.IsNullOrEmpty(records[0].SessionId) ? records[0].SessionId : null);
        double discount = CalculateOutcomeRiskDiscount(input.Outcome, "prefix-cache-break");

        List<(string SpanId, int TurnIndex, string Hash, long? FreshInput)> turns = [];
        int turnIdx = 0;
        foreach (CanonicalRecord r in records)
        {
            if (!string.Equals(r.Op, "llm.invoke", StringComparison.Ordinal))
                continue;

            string prefix = r.Content.TryGetValue("system_prompt", out string? sys) && sys.Length > 0
                ? sys
                : (r.Content.TryGetValue("instruction_context", out string? inst) ? inst : string.Empty);

            turns.Add((r.SpanId, turnIdx, prefix.Length > 0 ? HashNormalized(prefix) : string.Empty, r.Tokens.FreshInput));
            turnIdx++;
        }

        if (input.Turns is { Count: > 0 })
        {
            for (int i = 0; i < input.Turns.Count; i++)
            {
                PrefixTurnSupply t = input.Turns[i];
                string prefix = t.PrefixText ?? string.Empty;
                turns.Add((t.SpanId ?? $"turn-span-{i}", t.TurnIndex ?? i, prefix.Length > 0 ? HashNormalized(prefix) : string.Empty, t.FreshInput));
            }
        }

        if (turns.Count < 2)
            return [];

        for (int i = 0; i < turns.Count - 1; i++)
        {
            (string spanA, int idxA, string hashA, long? _) = turns[i];
            (string spanB, int idxB, string hashB, long? freshB) = turns[i + 1];

            if (hashA.Length == 0 || hashB.Length == 0 || string.Equals(hashA, hashB, StringComparison.Ordinal))
                continue;

            long? wasteTokens = freshB is > 0 ? freshB : null;
            bool measured = wasteTokens.HasValue;

            FindingEvidenceLink[] evidenceLinks =
            [
                new(spanA, idxA, $"Turn {idxA} established baseline prompt prefix (hash: {hashA[..Math.Min(10, hashA.Length)]}...)."),
                new(spanB, idxB, $"Turn {idxB} mutated prefix early in prompt (hash: {hashB[..Math.Min(10, hashB.Length)]}...), invalidating KV-cache reuse."),
            ];

            string recommendation = "Relocate dynamic tokens (e.g. timestamps, turn counters, environment variables) to the end of prompt messages, preserving an immutable prefix for KV-cache reuse.";
            string outcomeRiskCaveat = DeriveOutcomeRiskCaveat(
                input.Outcome,
                "prefix-cache-break",
                "Relocating dynamic instructions to the prompt tail can subtly shift model attention weights for early turn system prompts.");

            return
            [
                new Finding(
                    Id: $"finding-prefix-break-{sessionId ?? "session"}-{spanB}",
                    DetectorId: "prefix-cache-break",
                    Title: "Prefix Cache Break: Dynamic tokens injected early in prompt breaking KV-cache",
                    Mechanism: measured
                        ? $"Prefix tokens differed between turn {idxA} and turn {idxB}, breaking KV-cache prefix stability and forcing the model to re-evaluate {wasteTokens} fresh input tokens that could have been cached."
                        : $"Prefix tokens differed between turn {idxA} and turn {idxB}, breaking KV-cache prefix stability and forcing the model to re-evaluate a fresh input payload of unmeasured size that could have been cached.",
                    EvidenceLinks: evidenceLinks,
                    Confidence: "deterministic",
                    EstimatedWasteTokens: wasteTokens,
                    Recommendation: recommendation,
                    ErrorBar: null,
                    OutcomeRiskCaveat: outcomeRiskCaveat,
                    RunId: input.RunId,
                    SessionId: sessionId,
                    RankScore: ComputeRankScore(wasteTokens ?? 0, "deterministic", discount),
                    MeasurementClass: measured ? "deterministic" : "coverage-gap",
                    ConfidenceBasis: "Deterministically verified by whitespace-normalized SHA-256 hash divergence between consecutive turn prefixes.",
                    WhatWouldRaiseIt: measured
                        ? "Deterministic measurement; confidence is at ceiling."
                        : "Reported fresh-input token counters on the breaking turn would let the invalidated prefix cost be measured."),
            ];
        }

        return [];
    }

    /// <summary>Detector 5: session peak consumption exceeding 85% of a known (non-default/non-absent) context window.</summary>
    public static IReadOnlyList<Finding> DetectCompactionHazard(CompactionHazardInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        IReadOnlyList<CanonicalRecord> records = input.Records ?? [];
        string? invocationSession = string.IsNullOrEmpty(input.SessionId) ? null : input.SessionId;
        double discount = CalculateOutcomeRiskDiscount(input.Outcome, "compaction-hazard");

        string BaseKeyOf(CanonicalRecord record) =>
            !string.IsNullOrEmpty(record.SessionId)
                ? record.SessionId
                : (!string.IsNullOrEmpty(record.TraceId) ? record.TraceId : (invocationSession ?? "session"));

        Dictionary<string, HashSet<string>> harnessesByKey = new(StringComparer.Ordinal);
        foreach (CanonicalRecord record in records)
        {
            string? harness = CanonPricing.CanonicalHarnessId(record.Harness);
            if (harness is null)
                continue;

            string key = BaseKeyOf(record);
            if (!harnessesByKey.TryGetValue(key, out HashSet<string>? set))
            {
                set = new HashSet<string>(StringComparer.Ordinal);
                harnessesByKey[key] = set;
            }

            set.Add(harness);
        }

        Dictionary<string, CompactionSessionGroup> groups = new(StringComparer.Ordinal);
        CompactionSessionGroup GroupFor(string sid)
        {
            if (!groups.TryGetValue(sid, out CompactionSessionGroup? existing))
            {
                existing = new CompactionSessionGroup(sid);
                groups[sid] = existing;
            }

            return existing;
        }

        int turnIdx = 0;
        foreach (CanonicalRecord r in records)
        {
            if (!string.Equals(r.Op, "llm.invoke", StringComparison.Ordinal) || CanonPricing.CanonicalHarnessId(r.Harness) is null)
                continue;

            string key = BaseKeyOf(r);
            string? assigned = input.SessionIdentityLookup?.Invoke(key, r.Harness);
            string groupId = assigned ?? (harnessesByKey.TryGetValue(key, out HashSet<string>? set) && set.Count > 1
                ? $"{CanonPricing.NormalizeHarnessName(r.Harness)}:{key}"
                : key);

            long tokens = r.Tokens.ReportedInput > 0
                ? r.Tokens.ReportedInput
                : CanonPricing.MeasuredInput(r.Tokens);

            CompactionSessionGroup group = GroupFor(groupId);
            group.Turns.Add((r.SpanId, turnIdx, tokens));
            group.Records.Add(r);
            turnIdx++;
        }

        if (input.Turns is { Count: > 0 })
        {
            CompactionSessionGroup group = GroupFor(invocationSession ?? "session");
            for (int i = 0; i < input.Turns.Count; i++)
            {
                CompactionTurnSupply t = input.Turns[i];
                group.Turns.Add((t.SpanId ?? $"turn-span-{i}", t.TurnIndex ?? i, t.InputTokens ?? 0));
            }
        }

        List<Finding> findings = [];
        foreach (CompactionSessionGroup group in groups.Values)
        {
            if (group.Turns.Count == 0)
                continue;

            ContextWindowResult telemetryWindow = input.ContextLimit.HasValue
                ? new ContextWindowResult(input.ContextLimit.Value, "reported")
                : CanonPricing.ContextLimitOf(group.Records);

            ContextWindowResult? catalogWindow = string.Equals(telemetryWindow.ContextLimitSource, "default", StringComparison.Ordinal)
                ? input.ResolveWindow?.Invoke(group.Records)
                : null;
            ContextWindowResult window = catalogWindow ?? telemetryWindow;
            if (CanonPricing.IsUnnamedContextWindow(window.ContextLimitSource))
                continue;

            long limit = window.ContextLimit;
            bool isDeclared = string.Equals(window.ContextLimitSource, "declared", StringComparison.Ordinal);
            bool isCatalog = string.Equals(window.ContextLimitSource, "catalog", StringComparison.Ordinal);

            (string SpanId, int TurnIndex, long Tokens) peak = group.Turns[0];
            foreach ((string SpanId, int TurnIndex, long Tokens) t in group.Turns)
            {
                if (t.Tokens > peak.Tokens)
                    peak = t;
            }

            long thresholdTokens = (long)Math.Floor(limit * 0.85);
            if (peak.Tokens <= thresholdTokens)
                continue;

            double ratio = (double)peak.Tokens / limit;
            long estimatedWasteTokens = peak.Tokens - thresholdTokens;
            bool overWindow = peak.Tokens > limit;
            bool inferred = overWindow || isDeclared || isCatalog;
            string confidence = inferred ? "heuristic" : "deterministic";
            string measurementClass = inferred ? "inferred" : "deterministic";

            (string SpanId, int TurnIndex, long Tokens) prior = group.Turns.FirstOrDefault(t => t.TurnIndex != peak.TurnIndex);
            if (prior.SpanId is null)
                prior = group.Turns[0];

            FindingEvidenceLink[] evidenceLinks =
            [
                new(prior.SpanId, prior.TurnIndex, $"Turn {prior.TurnIndex} consumed {prior.Tokens} tokens ({Math.Round((double)prior.Tokens / limit * 100.0)}% capacity) approaching context limits."),
                new(peak.SpanId, peak.TurnIndex, $"Peak context consumption reached {peak.Tokens} tokens ({Math.Round(ratio * 100.0)}% capacity), exceeding the 85% compaction hazard threshold."),
            ];

            string recommendation = "Apply progressive disclosure and relocate historical conversation turns into structured checkpoints or rollups before reaching the 85% window limit.";
            const string baseCaveat = "Abrupt context compaction or summarization risks discarding early user constraints or domain definitions.";
            string outcomeRiskCaveat = DeriveOutcomeRiskCaveat(
                input.Outcome,
                "compaction-hazard",
                isCatalog
                    ? $"{baseCaveat} The window is a vendor catalog value, not a harness-reported measurement, so the percentage is inferred, never measured."
                    : baseCaveat);

            string windowPhrase = input.ContextLimit.HasValue
                ? "declared by the caller"
                : isCatalog
                    ? "taken from the vendor catalog"
                    : isDeclared
                        ? "declared for the session"
                        : "reported by session telemetry";

            string windowKind = isCatalog ? "vendor catalog" : isDeclared ? "declared" : "reported";
            string aggregateCaveat = overWindow
                ? $" The peak exceeds the {windowKind} window itself, so the turn's token attribution likely aggregates child calls rather than one turn's context; the spend is measured, the single-turn reading is inferred."
                : string.Empty;
            string declaredCaveat = isDeclared
                ? " The window is a declared window, not a harness-reported measurement, so the percentage is inferred."
                : string.Empty;
            string catalogCaveat = isCatalog
                ? " The window is a vendor catalog value, not a harness-reported measurement, so the percentage is inferred."
                : string.Empty;

            CultureInfo enUs = CultureInfo.GetCultureInfo("en-US");
            findings.Add(new Finding(
                Id: $"finding-compaction-hazard-{group.SessionId}-{peak.SpanId}",
                DetectorId: "compaction-hazard",
                Title: overWindow
                    ? $"Compaction Hazard: Context consumption exceeds the {windowKind} window without summarization plan"
                    : $"Compaction Hazard: Context consumption reached {Math.Round(ratio * 100.0)}% of window without summarization plan",
                Mechanism: $"Peak context consumption reached {peak.Tokens} tokens ({Math.Round(ratio * 100.0)}% of {limit} token window), exceeding the 85% safety boundary without active compaction or summarization. Window of record: {limit.ToString("N0", enUs)} tokens ({windowPhrase}).{aggregateCaveat}{declaredCaveat}{catalogCaveat}",
                EvidenceLinks: evidenceLinks,
                Confidence: confidence,
                EstimatedWasteTokens: estimatedWasteTokens,
                Recommendation: recommendation,
                ErrorBar: null,
                OutcomeRiskCaveat: outcomeRiskCaveat,
                RunId: input.RunId,
                SessionId: group.SessionId,
                RankScore: ComputeRankScore(estimatedWasteTokens, confidence, discount),
                MeasurementClass: measurementClass,
                ConfidenceBasis: overWindow
                    ? $"Peak turn input tokens exceed the {windowKind} context window, so single-turn attribution is inferred (likely aggregate of child calls) rather than measured."
                    : isCatalog
                        ? "Compared against a vendor catalog window; the spend is measured, but the percentage against a vendor catalog is inferred rather than measured."
                        : isDeclared
                            ? "Compared against the session-declared window; the spend is measured, but the percentage against a declared window is inferred rather than measured."
                            : "Deterministically measured by comparing the session peak turn input tokens against the context window in effect for that session.",
                WhatWouldRaiseIt: overWindow
                    ? $"Harness emission of per-turn input counters that reconcile with the {windowKind} window."
                    : isCatalog || isDeclared
                        ? "Harness reporting of the context window in session telemetry would let the percentage be measured."
                        : "Deterministic measurement; confidence is at ceiling.",
                Payload: new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["contextLimit"] = limit,
                    ["contextLimitSource"] = window.ContextLimitSource,
                }));
        }

        return findings;
    }

    /// <summary>Detector 6: subagent token share exceeding 60% without outcome justification.</summary>
    public static IReadOnlyList<Finding> DetectUnboundedDelegation(UnboundedDelegationInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        IReadOnlyList<CanonicalRecord> records = input.Records ?? [];
        string? sessionId = !string.IsNullOrEmpty(input.SessionId)
            ? input.SessionId
            : (records.Count > 0 && !string.IsNullOrEmpty(records[0].SessionId) ? records[0].SessionId : null);
        double discount = CalculateOutcomeRiskDiscount(input.Outcome, "unbounded-delegation");

        long rootTokens = input.RootTokens ?? 0;
        long subagentTokens = input.SubagentTokens ?? 0;
        string rootSpanId = input.RootSpanId ?? (records.Count > 0 ? records[0].SpanId : "root-span");
        string subagentSpanId = input.SubagentSpanId ?? "subagent-span";

        if (records.Count > 0 && rootTokens == 0 && subagentTokens == 0)
        {
            foreach (CanonicalRecord r in records)
            {
                long tokens = r.Tokens.ReportedInput + r.Tokens.Output;
                bool isSub = r.Raw is not null &&
                             r.Raw.TryGetValue("is_subagent", out JsonElement subEl) &&
                             subEl.ValueKind == JsonValueKind.True;
                if (isSub)
                {
                    subagentTokens += tokens;
                    subagentSpanId = r.SpanId;
                }
                else
                {
                    rootTokens += tokens;
                }
            }
        }

        long totalTokens = rootTokens + subagentTokens;
        if (totalTokens <= 0)
            return [];

        double delegationShare = (double)subagentTokens / totalTokens;
        if (delegationShare <= 0.60)
            return [];

        FindingEvidenceLink[] evidenceLinks =
        [
            new(rootSpanId, 0, $"Root execution initiated workflow requiring {rootTokens} tokens ({Math.Round((double)rootTokens / totalTokens * 100.0)}% share)."),
            new(subagentSpanId, 1, $"Delegated subagents consumed {subagentTokens} tokens ({Math.Round(delegationShare * 100.0)}% share), exceeding the 60% delegation ceiling."),
        ];

        string recommendation = "Relocate subagent scopes to focused, single-purpose tasks with on-demand handoffs, and defer child agent instantiation until specialized work is required.";
        string outcomeRiskCaveat = DeriveOutcomeRiskCaveat(
            input.Outcome,
            "unbounded-delegation",
            "Restricting delegation scopes may increase the root agent prompt size if complex tasks must be handled directly.");

        return
        [
            new Finding(
                Id: $"finding-unbounded-delegation-{input.RunId ?? sessionId ?? "workflow"}",
                DetectorId: "unbounded-delegation",
                Title: $"Unbounded Delegation: Subagent token share reached {Math.Round(delegationShare * 100.0)}% without outcome justification",
                Mechanism: $"Delegated executions consumed {subagentTokens} tokens ({Math.Round(delegationShare * 100.0)}% of total workflow), exceeding the 60% delegation ceiling without proportional outcome justification.",
                EvidenceLinks: evidenceLinks,
                Confidence: "calibrated_statistical",
                EstimatedWasteTokens: null,
                Recommendation: recommendation,
                ErrorBar: null,
                OutcomeRiskCaveat: outcomeRiskCaveat,
                RunId: input.RunId,
                SessionId: sessionId,
                RankScore: ComputeRankScore(0, "calibrated_statistical", discount),
                MeasurementClass: "inferred",
                ConfidenceBasis: "Calibrated from subagent token share relative to task outcome and delegation structure.",
                WhatWouldRaiseIt: "Harness emission of explicit subagent task justification spans and task completion status."),
        ];
    }

    /// <summary>Detector 7: listed skills never activated via Skill calls or <c>kyber.skills.invoked</c> (Decision D16).</summary>
    public static IReadOnlyList<Finding> DetectInactiveSkillReference(InactiveSkillReferenceInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        IReadOnlyList<CanonicalRecord> records = input.Records ?? [];
        string? sessionId = !string.IsNullOrEmpty(input.SessionId)
            ? input.SessionId
            : (records.Count > 0 && !string.IsNullOrEmpty(records[0].SessionId) ? records[0].SessionId : null);
        double discount = CalculateOutcomeRiskDiscount(input.Outcome, "inactive-skill-reference");

        List<CanonicalRecord> turnRecords = [];
        foreach (CanonicalRecord r in records)
        {
            if (string.Equals(r.Op, "llm.invoke", StringComparison.Ordinal))
                turnRecords.Add(r);
        }

        if (turnRecords.Count == 0)
            return [];

        IReadOnlyList<string>? listedNames = input.ReferencedSkills is not null
            ? input.ReferencedSkills
            : ReadListedSkillNames(records);
        IReadOnlyList<string>? invokedNames = input.ExecutedSkills is not null
            ? input.ExecutedSkills
            : ReadInvokedSkillNames(records);
        if (listedNames is null || invokedNames is null)
            return [];

        List<string> referenced = [];
        HashSet<string> seenReferenced = new(StringComparer.Ordinal);
        foreach (string rawName in listedNames)
        {
            string trimmed = rawName.Trim();
            if (trimmed.Length > 0 && seenReferenced.Add(trimmed))
                referenced.Add(trimmed);
        }

        if (referenced.Count == 0)
            return [];

        HashSet<string> executedSet = new(StringComparer.Ordinal);
        foreach (string rawName in invokedNames)
        {
            string trimmed = rawName.Trim();
            if (trimmed.Length > 0)
                executedSet.Add(trimmed);
        }

        CanonicalRecord firstTurn = turnRecords[0];
        int recordedTurns = turnRecords.Count;
        CanonicalRecord lastTurn = turnRecords[^1];
        int lastTurnIndex = recordedTurns - 1;

        List<Finding> findings = [];
        foreach (string skill in referenced)
        {
            if (executedSet.Contains(skill))
                continue;

            long? tokensPerTurn = input.SkillTokens is not null && input.SkillTokens.TryGetValue(skill, out long tpt) ? tpt : null;
            bool measured = tokensPerTurn is > 0;
            int turnsCount = recordedTurns;
            long? estimatedWasteTokens = measured ? tokensPerTurn!.Value * turnsCount : null;

            FindingEvidenceLink[] evidenceLinks = measured
                ?
                [
                    new(firstTurn.SpanId, 0, $"Skill instructions for \"{skill}\" loaded into prompt context ({tokensPerTurn} tokens/turn)."),
                    new(lastTurn.SpanId, turnsCount - 1, $"Turn {turnsCount - 1} completed with skill \"{skill}\" resident in prompt across {turnsCount} turns without activation."),
                ]
                :
                [
                    new(firstTurn.SpanId, 0, $"Skill \"{skill}\" listed (size unmeasured)."),
                    new(lastTurn.SpanId, lastTurnIndex, $"Turn {lastTurnIndex} is the last of {recordedTurns} recorded {(recordedTurns == 1 ? "turn" : "turns")}; skill \"{skill}\" was listed and not invoked in any of them."),
                ];

            string recommendation = $"Relocate skill instructions for \"{skill}\" to on-demand progressive disclosure, loading skill prompt blocks dynamically only when matching task intents occur.";
            string outcomeRiskCaveat = DeriveOutcomeRiskCaveat(
                input.Outcome,
                "inactive-skill-reference",
                "Per Decision D16, skill activation telemetry is not exported directly; skill instructions may supply latent guidance even when uncalled.");

            findings.Add(new Finding(
                Id: $"finding-inactive-skill-{skill}-{sessionId ?? "session"}",
                DetectorId: "inactive-skill-reference",
                Title: $"Inactive Skill Reference: Skill \"{skill}\" listed with 0 invocations",
                Mechanism: measured
                    ? $"Skill instruction block for \"{skill}\" remained resident in prompt context across {turnsCount} turns ({tokensPerTurn} tokens/turn), accumulating {estimatedWasteTokens} tokens without explicit execution or activation."
                    : $"Skill \"{skill}\" was listed in the session's {recordedTurns} recorded {(recordedTurns == 1 ? "turn" : "turns")} with 0 invocations; its context size was not measured.",
                EvidenceLinks: evidenceLinks,
                Confidence: "heuristic",
                EstimatedWasteTokens: estimatedWasteTokens,
                Recommendation: recommendation,
                ErrorBar: null,
                OutcomeRiskCaveat: outcomeRiskCaveat,
                RunId: input.RunId,
                SessionId: sessionId,
                RankScore: ComputeRankScore(estimatedWasteTokens ?? 0, "heuristic", discount),
                MeasurementClass: measured ? "inferred" : "coverage-gap",
                ConfidenceBasis: "Decision D16: Stamped at low confidence (heuristic) because a listed skill that is never loaded may still shape output, so activation is inferred from Skill calls and the invoked key.",
                WhatWouldRaiseIt: measured
                    ? "Harness export of skill activation spans for skills applied without a Skill tool call."
                    : "Reported skill-block token sizes would let the context size of the listed skill be measured. Harness export of discrete skill activation spans would also raise confidence."));
        }

        return findings;
    }

    /// <summary>Runs all seven detectors and returns Decision D6-ranked findings.</summary>
    public static IReadOnlyList<Finding> DetectFindings(DetectFindingsInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        List<Finding> all = [];

        DormantToolSchemaInput dormant = input.DormantToolSchema is null
            ? new DormantToolSchemaInput(input.RunId, input.SessionId, input.Records, input.Outcome)
            : input.DormantToolSchema with
            {
                RunId = input.DormantToolSchema.RunId ?? input.RunId,
                SessionId = input.DormantToolSchema.SessionId ?? input.SessionId,
                Records = input.DormantToolSchema.Records ?? input.Records,
                Outcome = input.DormantToolSchema.Outcome ?? input.Outcome,
            };
        all.AddRange(DetectDormantToolSchema(dormant));

        DuplicateToolCallInput duplicate = input.DuplicateToolCall is null
            ? new DuplicateToolCallInput(input.RunId, input.SessionId, input.Records, Outcome: input.Outcome)
            : input.DuplicateToolCall with
            {
                RunId = input.DuplicateToolCall.RunId ?? input.RunId,
                SessionId = input.DuplicateToolCall.SessionId ?? input.SessionId,
                Records = input.DuplicateToolCall.Records ?? input.Records,
                Outcome = input.DuplicateToolCall.Outcome ?? input.Outcome,
            };
        all.AddRange(DetectDuplicateToolCall(duplicate));

        OversizedToolResultInput oversized = input.OversizedToolResult is null
            ? new OversizedToolResultInput(input.RunId, input.SessionId, input.Records, Outcome: input.Outcome)
            : input.OversizedToolResult with
            {
                RunId = input.OversizedToolResult.RunId ?? input.RunId,
                SessionId = input.OversizedToolResult.SessionId ?? input.SessionId,
                Records = input.OversizedToolResult.Records ?? input.Records,
                Outcome = input.OversizedToolResult.Outcome ?? input.Outcome,
            };
        all.AddRange(DetectOversizedToolResult(oversized));

        PrefixCacheBreakInput prefix = input.PrefixCacheBreak is null
            ? new PrefixCacheBreakInput(input.RunId, input.SessionId, input.Records, Outcome: input.Outcome)
            : input.PrefixCacheBreak with
            {
                RunId = input.PrefixCacheBreak.RunId ?? input.RunId,
                SessionId = input.PrefixCacheBreak.SessionId ?? input.SessionId,
                Records = input.PrefixCacheBreak.Records ?? input.Records,
                Outcome = input.PrefixCacheBreak.Outcome ?? input.Outcome,
            };
        all.AddRange(DetectPrefixCacheBreak(prefix));

        CompactionHazardInput compaction = input.CompactionHazard is null
            ? new CompactionHazardInput(input.RunId, input.SessionId, input.Records, ContextLimit: input.ContextLimit, Outcome: input.Outcome, SessionIdentityLookup: input.SessionIdentityLookup, ResolveWindow: input.ResolveWindow)
            : input.CompactionHazard with
            {
                RunId = input.CompactionHazard.RunId ?? input.RunId,
                SessionId = input.CompactionHazard.SessionId ?? input.SessionId,
                Records = input.CompactionHazard.Records ?? input.Records,
                Outcome = input.CompactionHazard.Outcome ?? input.Outcome,
                ContextLimit = input.CompactionHazard.ContextLimit ?? input.ContextLimit,
                SessionIdentityLookup = input.CompactionHazard.SessionIdentityLookup ?? input.SessionIdentityLookup,
                ResolveWindow = input.CompactionHazard.ResolveWindow ?? input.ResolveWindow,
            };
        all.AddRange(DetectCompactionHazard(compaction));

        UnboundedDelegationInput delegation = input.UnboundedDelegation is null
            ? new UnboundedDelegationInput(input.RunId, input.SessionId, input.Records, Outcome: input.Outcome)
            : input.UnboundedDelegation with
            {
                RunId = input.UnboundedDelegation.RunId ?? input.RunId,
                SessionId = input.UnboundedDelegation.SessionId ?? input.SessionId,
                Records = input.UnboundedDelegation.Records ?? input.Records,
                Outcome = input.UnboundedDelegation.Outcome ?? input.Outcome,
            };
        all.AddRange(DetectUnboundedDelegation(delegation));

        InactiveSkillReferenceInput skills = input.InactiveSkillReference is null
            ? new InactiveSkillReferenceInput(input.RunId, input.SessionId, input.Records, Outcome: input.Outcome)
            : input.InactiveSkillReference with
            {
                RunId = input.InactiveSkillReference.RunId ?? input.RunId,
                SessionId = input.InactiveSkillReference.SessionId ?? input.SessionId,
                Records = input.InactiveSkillReference.Records ?? input.Records,
                Outcome = input.InactiveSkillReference.Outcome ?? input.Outcome,
            };
        all.AddRange(DetectInactiveSkillReference(skills));

        return RankFindings(all);
    }

    private static AvailabilityResult Measured() => new(true, "measured");

    private static AvailabilityResult NotMeasurable(string reason) => new(false, "not_measurable", reason);

    private static int ConfidenceOrder(string confidence) =>
        confidence switch
        {
            "deterministic" => 3,
            "calibrated_statistical" => 2,
            "heuristic" => 1,
            _ => 0,
        };

    private static IReadOnlyList<string>? ReadListedSkillNames(IReadOnlyList<CanonicalRecord> records)
    {
        HashSet<string> names = new(StringComparer.Ordinal);
        int measurableCount = 0;

        foreach (CanonicalRecord record in records)
        {
            if (!TryReadRecordListedSkills(record, out List<string> recordNames))
                continue;

            measurableCount++;
            foreach (string name in recordNames)
                names.Add(name);
        }

        return measurableCount == 0 ? null : [.. names];
    }

    private static bool TryReadRecordListedSkills(CanonicalRecord record, out List<string> skills)
    {
        skills = [];
        IReadOnlyDictionary<string, JsonElement>? raw = record.Raw;

        if (raw is not null && raw.TryGetValue(SkillsListedKey, out JsonElement claudeEl))
        {
            if (claudeEl.ValueKind != JsonValueKind.Object ||
                !claudeEl.TryGetProperty("isInitial", out JsonElement initEl) ||
                initEl.ValueKind is not (JsonValueKind.True or JsonValueKind.False) ||
                !claudeEl.TryGetProperty("skills", out JsonElement arrEl) ||
                arrEl.ValueKind != JsonValueKind.Array)
            {
                return false;
            }

            foreach (JsonElement item in arrEl.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.Object &&
                    item.TryGetProperty("name", out JsonElement nEl) &&
                    nEl.ValueKind == JsonValueKind.String &&
                    !string.IsNullOrEmpty(nEl.GetString()))
                {
                    skills.Add(nEl.GetString()!);
                }
            }

            return true;
        }

        if (raw is not null && raw.TryGetValue(CopilotListingKey, out JsonElement copilotEl))
            return TryParseStringArrayJson(copilotEl, out skills);

        if (raw is not null && raw.TryGetValue(AntigravityListingKey, out JsonElement agyEl))
        {
            if (agyEl.ValueKind != JsonValueKind.String)
                return false;
            string s = agyEl.GetString() ?? string.Empty;
            if (!s.StartsWith(AntigravityListingPrefix, StringComparison.Ordinal))
                return false;

            foreach (string part in s[AntigravityListingPrefix.Length..].Split(','))
            {
                string trimmed = part.Trim();
                if (trimmed.Length > 0)
                    skills.Add(trimmed);
            }

            return true;
        }

        if (raw is not null && raw.TryGetValue(OpenCodeListingKey, out JsonElement ocEl))
        {
            if (!TryParseStringArrayJson(ocEl, out skills))
                return false;
            if (skills.Count > 0)
                return true;
        }

        string promptText = SystemPromptText(record);
        if (string.Equals(CanonPricing.HarnessFamily(record.Harness), "opencode", StringComparison.Ordinal))
        {
            if (TryParseOpenCodeAvailableSkills(promptText, out bool hasBlock, out List<string> ids))
            {
                skills = ids;
                return true;
            }

            if (hasBlock)
                return false;
        }

        if (SkillsTagOpenRegex().IsMatch(promptText))
        {
            foreach (Match m in XmlSkillElementRegex().Matches(promptText))
            {
                Match nm = XmlNameElementRegex().Match(m.Groups[1].Value);
                if (nm.Success)
                {
                    string name = nm.Groups[1].Value.Trim();
                    if (name.Length > 0)
                        skills.Add(name);
                }
            }

            if (skills.Count > 0)
                return true;
        }

        if (string.Equals(CanonPricing.HarnessFamily(record.Harness), "claude-code", StringComparison.Ordinal) &&
            record.Parts is not null)
        {
            foreach (ContentPart part in record.Parts)
            {
                bool eligible = string.Equals(part.Part, "system_prompt", StringComparison.Ordinal) ||
                                (string.Equals(part.Part, "conversation_history", StringComparison.Ordinal) &&
                                 string.Equals(part.Role, "system", StringComparison.Ordinal));
                if (!eligible || string.IsNullOrEmpty(part.Text))
                    continue;

                List<string> parsedBody = ParseClaudeBodyListing(part.Text);
                if (parsedBody.Count > 0)
                {
                    skills = parsedBody;
                    return true;
                }
            }
        }

        return false;
    }

    private static bool TryParseStringArrayJson(JsonElement el, out List<string> skills)
    {
        skills = [];
        if (el.ValueKind != JsonValueKind.String)
            return false;

        try
        {
            using JsonDocument doc = JsonDocument.Parse(el.GetString() ?? string.Empty);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
                return false;

            foreach (JsonElement item in doc.RootElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.String)
                    return false;
                string val = item.GetString() ?? string.Empty;
                if (val.Length > 0)
                    skills.Add(val);
            }

            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool TryParseOpenCodeAvailableSkills(string text, out bool hasBlock, out List<string> ids)
    {
        ids = [];
        hasBlock = false;
        const string openTag = "<available_skills>";
        const string closeTag = "</available_skills>";
        int open = text.LastIndexOf(openTag, StringComparison.Ordinal);
        if (open < 0)
            return false;

        hasBlock = true;
        int start = open + openTag.Length;
        int close = text.IndexOf(closeTag, start, StringComparison.Ordinal);
        if (close < 0)
            return false;

        string body = text[start..close];
        int openCount = CountSubstring(body, "<skill>");
        int closeCount = CountSubstring(body, "</skill>");
        if (openCount != closeCount)
            return false;

        foreach (Match match in OpenCodeSkillElementRegex().Matches(body))
        {
            string withoutDesc = DescriptionElementRegex().Replace(match.Groups[1].Value, string.Empty);
            Match idMatch = IdElementRegex().Match(withoutDesc);
            string trimmed = idMatch.Success ? idMatch.Groups[1].Value.Trim() : string.Empty;
            if (trimmed.Length == 0)
                return false;
            ids.Add(trimmed);
        }

        return ids.Count == openCount;
    }

    private static int CountSubstring(string haystack, string needle)
    {
        int count = 0;
        int idx = 0;
        while ((idx = haystack.IndexOf(needle, idx, StringComparison.Ordinal)) >= 0)
        {
            count++;
            idx += needle.Length;
        }

        return count;
    }

    private static string SystemPromptText(CanonicalRecord record)
    {
        if (record.Parts is not null)
        {
            StringBuilder sb = new();
            foreach (ContentPart part in record.Parts)
            {
                if (string.Equals(part.Part, "system_prompt", StringComparison.Ordinal) && !string.IsNullOrEmpty(part.Text))
                {
                    if (sb.Length > 0)
                        sb.Append('\n');
                    sb.Append(part.Text);
                }
            }

            return sb.ToString();
        }

        return record.Content.TryGetValue("system_prompt", out string? s) ? s : string.Empty;
    }

    private static List<string> ParseClaudeBodyListing(string text)
    {
        int at = text.IndexOf(BodyListingMarker, StringComparison.Ordinal);
        if (at < 0)
            return [];

        List<string> names = [];
        foreach (string rawLine in text[(at + BodyListingMarker.Length)..].Split('\n'))
        {
            string trimmed = rawLine.Trim();
            if (trimmed.Length == 0)
            {
                if (names.Count > 0)
                    break;
                continue;
            }

            if (names.Count > 0 && rawLine.Length > 0 && char.IsWhiteSpace(rawLine[0]))
                continue;

            Match entry = BodySkillEntryRegex().Match(rawLine.TrimEnd());
            if (!entry.Success)
            {
                if (names.Count > 0)
                    break;
                continue;
            }

            names.Add(entry.Groups[1].Value);
        }

        return names;
    }

    private static IReadOnlyList<string>? ReadInvokedSkillNames(IReadOnlyList<CanonicalRecord> records)
    {
        bool hasInvocationSource = false;
        HashSet<string> invoked = new(StringComparer.Ordinal);

        foreach (CanonicalRecord record in records)
        {
            bool isClaude = string.Equals(CanonPricing.HarnessFamily(record.Harness), "claude-code", StringComparison.Ordinal);
            bool isSkillCall = string.Equals(record.Op, "tool.invoke", StringComparison.Ordinal) &&
                               (string.Equals(record.Name, "Skill", StringComparison.Ordinal) ||
                                string.Equals(record.Name, "skill", StringComparison.Ordinal));
            bool hasKeyArray = record.Raw is not null &&
                               record.Raw.TryGetValue(SkillsInvokedKey, out JsonElement invArr) &&
                               invArr.ValueKind == JsonValueKind.Array;

            if (isClaude || isSkillCall || hasKeyArray)
                hasInvocationSource = true;

            if (isSkillCall && record.Raw is not null && record.Raw.TryGetValue("arguments", out JsonElement argsEl))
            {
                string? name = ExtractSkillArgument(argsEl);
                if (!string.IsNullOrEmpty(name))
                    invoked.Add(name);
            }

            if (hasKeyArray && record.Raw is not null && record.Raw.TryGetValue(SkillsInvokedKey, out JsonElement arr))
            {
                foreach (JsonElement el in arr.EnumerateArray())
                {
                    if (el.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(el.GetString()))
                        invoked.Add(el.GetString()!);
                }
            }
        }

        return hasInvocationSource ? [.. invoked] : null;
    }

    private static string? ExtractSkillArgument(JsonElement argsEl)
    {
        JsonElement obj = argsEl;
        JsonDocument? parsed = null;
        try
        {
            if (argsEl.ValueKind == JsonValueKind.String)
            {
                try
                {
                    parsed = JsonDocument.Parse(argsEl.GetString() ?? string.Empty);
                    obj = parsed.RootElement;
                }
                catch (JsonException)
                {
                    return null;
                }
            }

            if (obj.ValueKind != JsonValueKind.Object)
                return null;

            if (obj.TryGetProperty("skill", out JsonElement skillEl) && skillEl.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(skillEl.GetString()))
                return skillEl.GetString();

            if (obj.TryGetProperty("name", out JsonElement nameEl) && nameEl.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(nameEl.GetString()))
                return nameEl.GetString();

            return null;
        }
        finally
        {
            parsed?.Dispose();
        }
    }

    private static TestSuiteCounts? ParseTestCounts(string text)
    {
        Match m0 = VitestHeaderTestRegex().Match(text);
        if (m0.Success)
        {
            long failed = m0.Groups[1].Success ? long.Parse(m0.Groups[1].Value, CultureInfo.InvariantCulture) : 0;
            long passed = long.Parse(m0.Groups[2].Value, CultureInfo.InvariantCulture);
            long total = m0.Groups[3].Success ? long.Parse(m0.Groups[3].Value, CultureInfo.InvariantCulture) : passed + failed;
            return new TestSuiteCounts(passed, failed, total);
        }

        Match m1 = VitestIconTestRegex().Match(text);
        if (m1.Success)
        {
            long passed = long.Parse(m1.Groups[1].Value, CultureInfo.InvariantCulture);
            long failed = m1.Groups[2].Success ? long.Parse(m1.Groups[2].Value, CultureInfo.InvariantCulture) : 0;
            return new TestSuiteCounts(passed, failed, passed + failed);
        }

        Match m2 = PytestTestRegex().Match(text);
        if (m2.Success)
        {
            long failed = m2.Groups[1].Success ? long.Parse(m2.Groups[1].Value, CultureInfo.InvariantCulture) : 0;
            long passed = long.Parse(m2.Groups[2].Value, CultureInfo.InvariantCulture);
            return new TestSuiteCounts(passed, failed, passed + failed);
        }

        Match m3 = GenericTestRegex().Match(text);
        if (m3.Success)
        {
            long passed = long.Parse(m3.Groups[1].Value, CultureInfo.InvariantCulture);
            long failed = long.Parse(m3.Groups[2].Value, CultureInfo.InvariantCulture);
            return new TestSuiteCounts(passed, failed, passed + failed);
        }

        Match m4 = CargoGoTestRegex().Match(text);
        if (m4.Success)
        {
            long passed = long.Parse(m4.Groups[1].Value, CultureInfo.InvariantCulture);
            long failed = long.Parse(m4.Groups[2].Value, CultureInfo.InvariantCulture);
            return new TestSuiteCounts(passed, failed, passed + failed);
        }

        return null;
    }

    private static bool TryGetRawElement(CanonicalRecord record, string[] keys, out JsonElement element)
    {
        if (record.Raw is not null)
        {
            foreach (string key in keys)
            {
                if (record.Raw.TryGetValue(key, out element) && element.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined))
                    return true;
            }
        }

        element = default;
        return false;
    }

    private static string? RawString(CanonicalRecord record, string[] keys)
    {
        if (!TryGetRawElement(record, keys, out JsonElement el))
            return null;

        if (el.ValueKind == JsonValueKind.String)
        {
            string? s = el.GetString();
            return string.IsNullOrEmpty(s) ? null : s;
        }

        if (el.ValueKind == JsonValueKind.Number && el.TryGetDouble(out double d) && double.IsFinite(d))
            return d.ToString(CultureInfo.InvariantCulture);

        return null;
    }

    private static bool TryGetRawNumber(CanonicalRecord record, string[] keys, out double number)
    {
        if (record.Raw is not null)
        {
            foreach (string key in keys)
            {
                if (!record.Raw.TryGetValue(key, out JsonElement el))
                    continue;

                if (el.ValueKind == JsonValueKind.Number && el.TryGetDouble(out number) && double.IsFinite(number))
                    return true;

                if (el.ValueKind == JsonValueKind.String &&
                    double.TryParse(el.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out number) &&
                    double.IsFinite(number))
                {
                    return true;
                }
            }
        }

        number = 0;
        return false;
    }

    private static bool TryGetRawBool(CanonicalRecord record, string[] keys, out bool flag)
    {
        if (record.Raw is not null)
        {
            foreach (string key in keys)
            {
                if (record.Raw.TryGetValue(key, out JsonElement el) && el.ValueKind is JsonValueKind.True or JsonValueKind.False)
                {
                    flag = el.ValueKind == JsonValueKind.True;
                    return true;
                }
            }
        }

        flag = false;
        return false;
    }

    private class ToolResidency
    {
        public List<(string SpanId, int TurnIndex)> Turns { get; } = [];
        public long? Tokens { get; set; }
    }

    private record NormalizedToolCall(
        string Name,
        string Args,
        string? ArgsHash,
        string SpanId,
        int TurnIndex,
        long? Tokens,
        string? SessionId);

    private record ToolResultItem(
        string ToolName,
        long? Tokens,
        int Chars,
        long Bytes,
        string SpanId,
        int TurnIndex,
        string ConsumingSpanId,
        string? SessionId)
    {
        public string tokensDisplay => Tokens.HasValue
            ? $"{Tokens} tokens, {Chars} characters, {Bytes} bytes"
            : $"{Chars} characters, {Bytes} bytes";
    }

    private class CompactionSessionGroup(string sessionId)
    {
        public string SessionId { get; } = sessionId;
        public List<(string SpanId, int TurnIndex, long Tokens)> Turns { get; } = [];
        public List<CanonicalRecord> Records { get; } = [];
    }
}
