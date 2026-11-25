using System.Text.Json;
using KyberDash.Domain;
using Xunit;

namespace KyberDash.Application.Tests.Projection;

/// <summary>
/// Decision D15: findings and context analysis never publish a figure the harness did not report
/// and that is not exact arithmetic on reported values. Error bands, heuristic floors and ratios,
/// and jumps measured against an unmeasured turn all show as not measured instead.
/// </summary>
public class NoEstimateFindingsTests
{
    /// <summary>The subagent-waste finding carries no waste figure and no band, and ranks on confidence alone.</summary>
    [Fact]
    public void UnboundedDelegation_EmitsNoWasteFigureAndNoErrorBar()
    {
        IReadOnlyList<Finding> findings = CanonFindings.DetectUnboundedDelegation(new UnboundedDelegationInput(
            SessionId: "sess-delegate",
            RootTokens: 1_000,
            SubagentTokens: 9_000));

        Finding finding = Assert.Single(findings);
        Assert.Null(finding.EstimatedWasteTokens);
        Assert.Null(finding.ErrorBar);
        Assert.Equal(0.0, finding.RankScore);
    }

    /// <summary>A token overrun reports exact arithmetic on the reported count, with no floor and no band.</summary>
    [Fact]
    public void OversizedToolResult_TokenOverrunIsExactDifferenceWithoutFloorOrBand()
    {
        IReadOnlyList<Finding> findings = CanonFindings.DetectOversizedToolResult(new OversizedToolResultInput(
            SessionId: "sess-oversized",
            Results: [new ToolResultSupply("Read", "body", Tokens: 4_050, Characters: 10, Bytes: 10)],
            TokenThreshold: 4_000));

        Finding finding = Assert.Single(findings);
        Assert.Equal(50, finding.EstimatedWasteTokens);
        Assert.Null(finding.ErrorBar);
    }

    /// <summary>A bytes-only overrun has no reported token count, so no token figure is derived from the bytes.</summary>
    [Fact]
    public void OversizedToolResult_ByteOnlyOverrunHasNoTokenFigure()
    {
        IReadOnlyList<Finding> findings = CanonFindings.DetectOversizedToolResult(new OversizedToolResultInput(
            SessionId: "sess-oversized",
            Results: [new ToolResultSupply("Read", "body", Tokens: null, Characters: 10, Bytes: 40_000)],
            TokenThreshold: 4_000,
            ByteThreshold: 1_000));

        Finding finding = Assert.Single(findings);
        Assert.Null(finding.EstimatedWasteTokens);
        Assert.Null(finding.ErrorBar);
    }

    /// <summary>A tool result with text but no reported token count is never sized from its text length.</summary>
    [Fact]
    public void OversizedToolResult_RecordWithoutReportedTokensIsNotSizedFromText()
    {
        CanonicalRecord tool = ToolRecord("tool-1", "Read", parts: [new ContentPart("tool_result_content", new string('x', 40_000))]);

        IReadOnlyList<Finding> findings = CanonFindings.DetectOversizedToolResult(new OversizedToolResultInput(
            SessionId: "sess-oversized",
            Records: [LlmRecord("turn-1", freshInput: 10), tool],
            TokenThreshold: 1_000,
            CharThreshold: 100_000,
            ByteThreshold: 1_000_000));

        Assert.Empty(findings);
    }

    /// <summary>Duplicate calls with only result text report no token figure.</summary>
    [Fact]
    public void DuplicateToolCall_TextOnlyResultHasNoTokenFigure()
    {
        Dictionary<string, JsonElement> raw = new(StringComparer.Ordinal)
        {
            ["arguments"] = JsonSerializer.SerializeToElement(new { path = "a.txt" }),
            ["result"] = JsonSerializer.SerializeToElement(new string('y', 4_000)),
            ["gen_ai.tool.result_bytes"] = JsonSerializer.SerializeToElement(4_000),
        };

        IReadOnlyList<Finding> findings = CanonFindings.DetectDuplicateToolCall(new DuplicateToolCallInput(
            SessionId: "sess-dup",
            Records:
            [
                LlmRecord("turn-1", freshInput: 10),
                ToolRecord("tool-1", "Read", raw: raw),
                LlmRecord("turn-2", freshInput: 10),
                ToolRecord("tool-2", "Read", raw: raw),
            ]));

        Finding finding = Assert.Single(findings);
        Assert.Null(finding.EstimatedWasteTokens);
        Assert.Null(finding.ErrorBar);
    }

    /// <summary>Schema residency waste is exact schema tokens times resident turns, with no band.</summary>
    [Fact]
    public void DormantToolSchema_WasteIsExactSchemaTokensTimesTurnsWithoutBand()
    {
        IReadOnlyList<Finding> findings = CanonFindings.DetectDormantToolSchema(new DormantToolSchemaInput(
            SessionId: "sess-dormant",
            ToolDefinitions: [new ToolDefinitionSupply("never_called", Tokens: 600)],
            TurnsCount: 4,
            Invocations: []));

        Finding finding = Assert.Single(findings);
        Assert.Equal(2_400, finding.EstimatedWasteTokens);
        Assert.Null(finding.ErrorBar);
    }

    /// <summary>Residency is not invented: a supply with no turn count and no recorded turns yields no finding.</summary>
    [Fact]
    public void DormantToolSchema_DoesNotInventResidentTurns()
    {
        IReadOnlyList<Finding> findings = CanonFindings.DetectDormantToolSchema(new DormantToolSchemaInput(
            SessionId: "sess-dormant",
            ToolDefinitions: [new ToolDefinitionSupply("never_called", Tokens: 600)],
            Invocations: []));

        Assert.Empty(findings);
    }

    /// <summary>Skill residency waste uses the recorded turn count, never a floor of three turns.</summary>
    [Fact]
    public void InactiveSkill_WasteUsesRecordedTurnsWithoutFloorOrBand()
    {
        IReadOnlyList<Finding> findings = CanonFindings.DetectInactiveSkillReference(new InactiveSkillReferenceInput(
            SessionId: "sess-skill",
            Records: [LlmRecord("turn-1", freshInput: 10)],
            ReferencedSkills: ["docs"],
            ExecutedSkills: [],
            SkillTokens: new Dictionary<string, long>(StringComparer.Ordinal) { ["docs"] = 350 }));

        Finding finding = Assert.Single(findings);
        Assert.Equal(350, finding.EstimatedWasteTokens);
        Assert.Null(finding.ErrorBar);
    }

    /// <summary>A turn that follows an unmeasured turn is never flagged as a jump from zero.</summary>
    [Fact]
    public void AnalyzeContext_NoJumpIsFlaggedAgainstAnUnmeasuredTurn()
    {
        ContextAnalysis context = CanonContextAnalysis.AnalyzeContext(
            [ContextTurnOf(inputTokens: 500, freshInput: null), ContextTurnOf(inputTokens: 6_000, freshInput: 5_000)],
            new ContextCountOptions(ContextLimit: 10_000, ContextLimitSource: "telemetry"),
            static (text, _) => new TokenCount(text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).LongLength, "word-count", false));

        Assert.Null(context.Turns[0].FreshInput);
        Assert.Equal(5_000, context.Turns[1].FreshInput);
        Assert.Null(context.Turns[1].FreshInputJump);
    }

    /// <summary>The jump baseline skips an unmeasured turn and uses the previous measured turn.</summary>
    [Fact]
    public void AnalyzeContext_JumpBaselineIsThePreviousMeasuredTurn()
    {
        ContextAnalysis context = CanonContextAnalysis.AnalyzeContext(
            [
                ContextTurnOf(inputTokens: 1_100, freshInput: 1_000),
                ContextTurnOf(inputTokens: 1_200, freshInput: null),
                ContextTurnOf(inputTokens: 6_000, freshInput: 5_000),
            ],
            new ContextCountOptions(ContextLimit: 10_000, ContextLimitSource: "telemetry"),
            static (text, _) => new TokenCount(text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).LongLength, "word-count", false));

        Assert.Null(context.Turns[1].FreshInputJump);
        FreshInputJump jump = Assert.IsType<FreshInputJump>(context.Turns[2].FreshInputJump);
        Assert.Equal(1_000, jump.Previous);
        Assert.Equal(5.0, jump.Factor);
    }

    /// <summary>A published-rate cost is a calculation: the call flag and its serialized key say so.</summary>
    [Fact]
    public void SynthesizedCall_MarksPublishedRateCostAsCalculated()
    {
        ParsedProviderCall call = new()
        {
            Timestamp = "2026-11-14T10:01:00.000Z",
            Provider = "pi",
            Model = "gpt-5",
            InputTokens = 100,
            OutputTokens = 60,
            CostUsd = 0.12,
            CostIsCalculated = true,
            SessionId = "sess-pi",
            DeduplicationKey = "pi:sess-pi:call-9",
        };

        CanonicalRecord record = CanonSynth.SynthesizeCall(call);

        Assert.Equal(CostBasis.Published, record.Cost.Basis);
        Assert.NotNull(record.Raw);
        Assert.True(record.Raw!["costIsCalculated"].GetBoolean());
        Assert.False(record.Raw.ContainsKey("costIsEstimated"));
    }

    private static ContextTurn ContextTurnOf(long inputTokens, long? freshInput) =>
        new([new ContextPart("system_prompt", string.Empty, Tokens: inputTokens)], inputTokens, freshInput);

    private static CanonicalRecord LlmRecord(string spanId, long freshInput) =>
        new()
        {
            SpanId = spanId,
            Source = "test",
            Harness = "claude-code",
            SessionId = "sess",
            Name = "llm.invoke",
            Op = "llm.invoke",
            Kind = "client",
            Timestamp = "2026-11-24T10:00:00.000Z",
            Status = "ok",
            Tokens = new TokenUsage(freshInput, 0, 0, 5, freshInput, 5),
            Content = new Dictionary<string, string>(StringComparer.Ordinal),
            Cost = new CostBlock(CostBasis.Unknown, CostStatus.NoRate),
        };

    private static CanonicalRecord ToolRecord(
        string spanId,
        string name,
        IReadOnlyList<ContentPart>? parts = null,
        IReadOnlyDictionary<string, JsonElement>? raw = null) =>
        new()
        {
            SpanId = spanId,
            Source = "test",
            Harness = "claude-code",
            SessionId = "sess",
            Name = name,
            Op = "tool.invoke",
            Kind = "internal",
            Timestamp = "2026-11-24T10:00:01.000Z",
            Status = "ok",
            Tokens = new TokenUsage(0, 0, 0, 0, 0, 0),
            Content = new Dictionary<string, string>(StringComparer.Ordinal),
            Cost = new CostBlock(CostBasis.Unknown, CostStatus.NoRate),
            Parts = parts,
            Raw = raw,
        };
}
