using KyberDash.Data.Collector;
using KyberDash.Domain;
using Xunit;

namespace KyberDash.Application.Tests.Projection;

/// <summary>
/// Verifies the shared record-hydration rule both relational read paths call
/// (<see cref="CanonRecordHydration"/>): typed <see cref="CollectedRecord"/> columns become the
/// canonical analysis record with the token, part, tool-definition, cost, and raw-attribute
/// fields the pipeline reads.
/// </summary>
public class CanonRecordHydrationTests
{
    private static readonly DateTimeOffset StartedAt = new(2026, 11, 14, 10, 30, 0, TimeSpan.Zero);

    [Fact]
    public void HydrateRejectsANullRecord()
    {
        Assert.Throws<ArgumentNullException>(() => CanonRecordHydration.Hydrate(null!));
    }

    [Fact]
    public void HydrateMapsIdentityColumnsOpKindStatusAndTimestamp()
    {
        CanonicalRecord hydrated = CanonRecordHydration.Hydrate(
            Record(op: RecordOp.AgentInvoke, kind: RecordKind.Producer, status: RecordStatus.Error));

        Assert.Equal("span-1", hydrated.SpanId);
        Assert.Equal("trace-1", hydrated.TraceId);
        Assert.Equal("parent-1", hydrated.ParentSpanId);
        Assert.Equal("relational", hydrated.Source);
        Assert.Equal("claude-code", hydrated.Harness);
        Assert.Equal("sess-1", hydrated.SessionId);
        Assert.Equal("agent turn", hydrated.Name);
        Assert.Equal("agent.invoke", hydrated.Op);
        Assert.Equal("producer", hydrated.Kind);
        Assert.Equal("error", hydrated.Status);
        Assert.Equal("2026-11-14T10:30:00.000Z", hydrated.Timestamp);
        Assert.Equal(250, hydrated.DurationMs);
    }

    [Fact]
    public void HydrateMapsEveryTokenClassAndReportedTotals()
    {
        CollectedRecord record = Record() with
        {
            Tokens =
            [
                new CollectedTokenCount(TokenClass.Input, 11),
                new CollectedTokenCount(TokenClass.CacheRead, 22),
                new CollectedTokenCount(TokenClass.CacheWrite, 33),
                new CollectedTokenCount(TokenClass.Output, 44),
                new CollectedTokenCount(TokenClass.Reasoning, 5),
            ],
            ReportedInputTokens = 66,
            ReportedOutputTokens = 44,
        };

        CanonicalRecord hydrated = CanonRecordHydration.Hydrate(record);

        Assert.Equal(11, hydrated.Tokens.FreshInput);
        Assert.Equal(22, hydrated.Tokens.CacheRead);
        Assert.Equal(33, hydrated.Tokens.CacheCreation);
        Assert.Equal(44, hydrated.Tokens.Output);
        Assert.Equal(5, hydrated.Tokens.Reasoning);
        Assert.Equal(66, hydrated.Tokens.ReportedInput);
        Assert.Equal(44, hydrated.Tokens.ReportedOutput);
    }

    [Fact]
    public void HydrateDefaultsMissingTokenCountsToZeroAndNullReasoning()
    {
        CanonicalRecord hydrated = CanonRecordHydration.Hydrate(Record());

        Assert.Equal(0, hydrated.Tokens.FreshInput);
        Assert.Equal(0, hydrated.Tokens.CacheRead);
        Assert.Equal(0, hydrated.Tokens.CacheCreation);
        Assert.Equal(0, hydrated.Tokens.Output);
        Assert.Null(hydrated.Tokens.Reasoning);
        Assert.Equal(0, hydrated.Tokens.ReportedInput);
        Assert.Equal(0, hydrated.Tokens.ReportedOutput);
    }

    [Fact]
    public void HydrateDefaultsReportedDurationToZeroWhenUnset()
    {
        CanonicalRecord hydrated = CanonRecordHydration.Hydrate(Record() with { ReportedDurationMs = null });

        Assert.Equal(0, hydrated.DurationMs);
    }

    [Fact]
    public void HydrateBuildsRawAttributesForModelAgentAndDeclaredWindow()
    {
        CollectedRecord record = Record() with
        {
            Model = "gloss-1",
            Agent = new CollectedAgentInvocation(AgentName: "planner", SpawningCallId: null, Succeeded: true, Error: null),
            Signals = new CollectedRecordSignals(
                ErrorMessage: null,
                EditFailures: null,
                CorrectionRule: null,
                DeclaredContextWindow: 200_000),
        };

        CanonicalRecord hydrated = CanonRecordHydration.Hydrate(record);

        Assert.NotNull(hydrated.Raw);
        Assert.Equal(3, hydrated.Raw.Count);
        Assert.Equal(200_000, hydrated.Raw[CanonPricing.DeclaredContextLimitKey].GetInt64());
        Assert.Equal("gloss-1", hydrated.Raw["gen_ai.response.model"].GetString());
        Assert.Equal("planner", hydrated.Raw["gen_ai.agent.name"].GetString());
    }

    [Fact]
    public void HydrateOmitsRawAttributesForBlankModelAgentAndUnsetWindow()
    {
        CollectedRecord blank = Record() with
        {
            Model = "  ",
            Signals = new CollectedRecordSignals(
                ErrorMessage: null,
                EditFailures: null,
                CorrectionRule: null,
                DeclaredContextWindow: 0),
        };

        CanonicalRecord hydrated = CanonRecordHydration.Hydrate(blank);

        Assert.NotNull(hydrated.Raw);
        Assert.Empty(hydrated.Raw);
    }

    [Fact]
    public void HydrateOrdersContentPartsBeforeToolDefinitions()
    {
        CollectedRecord record = Record() with
        {
            Parts =
            [
                new CollectedContentPart(
                    Position: 1,
                    Bucket: ContentBucket.UserPrompt,
                    PlainText: "second",
                    Withheld: null,
                    ReportedTokens: 4,
                    Server: null,
                    Role: "user",
                    Resident: true,
                    OriginalUtf8BytesWhenTruncated: null),
                new CollectedContentPart(
                    Position: 0,
                    Bucket: ContentBucket.SystemPrompt,
                    PlainText: "first",
                    Withheld: null,
                    ReportedTokens: 3,
                    Server: "stdio",
                    Role: "system",
                    Resident: null,
                    OriginalUtf8BytesWhenTruncated: 512),
                new CollectedContentPart(
                    Position: 2,
                    Bucket: ContentBucket.Reasoning,
                    PlainText: null,
                    Withheld: new CollectedWithheldText(2048, 512, 17),
                    ReportedTokens: null,
                    Server: null,
                    Role: null,
                    Resident: null,
                    OriginalUtf8BytesWhenTruncated: null),
            ],
            ToolDefinitions =
            [
                new CollectedToolDefinition(Position: 1, ToolName: "tool-b", Server: "stdio", SchemaTokens: 9),
                new CollectedToolDefinition(Position: 0, ToolName: "tool-a", Server: "stdio", SchemaTokens: 7),
            ],
        };

        CanonicalRecord hydrated = CanonRecordHydration.Hydrate(record);

        Assert.NotNull(hydrated.Parts);
        Assert.Equal(5, hydrated.Parts.Count);
        Assert.Equal("system_prompt", hydrated.Parts[0].Part);
        Assert.Equal("first", hydrated.Parts[0].Text);
        Assert.Equal(3, hydrated.Parts[0].Tokens);
        Assert.Equal("stdio", hydrated.Parts[0].Server);
        Assert.Equal("system", hydrated.Parts[0].Role);
        Assert.Equal(0, hydrated.Parts[0].Order);
        Assert.True(hydrated.Parts[0].Truncated);

        Assert.Equal("user_prompt", hydrated.Parts[1].Part);
        Assert.Equal("second", hydrated.Parts[1].Text);
        Assert.Equal(1, hydrated.Parts[1].Order);

        Assert.Equal("reasoning", hydrated.Parts[2].Part);
        Assert.Equal(string.Empty, hydrated.Parts[2].Text);
        Assert.Equal(17, hydrated.Parts[2].Tokens);
        Assert.Equal(2, hydrated.Parts[2].Order);

        Assert.Equal("tool_definitions", hydrated.Parts[3].Part);
        Assert.Equal("tool-a", hydrated.Parts[3].Text);
        Assert.Equal(7, hydrated.Parts[3].Tokens);
        Assert.Equal(1_000, hydrated.Parts[3].Order);

        Assert.Equal("tool_definitions", hydrated.Parts[4].Part);
        Assert.Equal("tool-b", hydrated.Parts[4].Text);
        Assert.Equal(9, hydrated.Parts[4].Tokens);
        Assert.Equal(1_001, hydrated.Parts[4].Order);
    }

    [Fact]
    public void HydratePricesHarnessReportedCostAndDefaultsUnpricedRecords()
    {
        CanonicalRecord priced = CanonRecordHydration.Hydrate(Record() with
        {
            ReportedCost = new CollectedCost(Amount: 0.25, Currency: "USD"),
        });

        Assert.Equal(CostBasis.Harness, priced.Cost.Basis);
        Assert.Equal(CostStatus.Priced, priced.Cost.Status);
        Assert.Equal(0.25, priced.Cost.Value);
        Assert.Equal("USD", priced.Cost.Currency);

        CanonicalRecord unpriced = CanonRecordHydration.Hydrate(Record());

        Assert.Equal(CostBasis.Unknown, unpriced.Cost.Basis);
        Assert.Equal(CostStatus.NoRate, unpriced.Cost.Status);
        Assert.Null(unpriced.Cost.Value);
        Assert.Null(unpriced.Cost.Currency);
    }

    private static CollectedRecord Record(
        RecordOp op = RecordOp.LlmInvoke,
        RecordKind kind = RecordKind.Internal,
        RecordStatus status = RecordStatus.Ok) =>
        new(
            RecordId: "span-1",
            HarnessId: "claude-code",
            Op: op,
            Kind: kind,
            Name: "agent turn",
            StartedAt: StartedAt,
            Status: status,
            SessionKey: "sess-1",
            ParentRecordId: "parent-1",
            TraceId: "trace-1",
            Model: null,
            EndedAt: null,
            ReportedDurationMs: 250,
            Tokens: [],
            ReportedCost: null,
            Parts: [],
            Tool: null,
            Agent: null,
            SkillsInvoked: [],
            AgentInstanceId: null,
            ToolDefinitions: [],
            Signals: new CollectedRecordSignals(
                ErrorMessage: null,
                EditFailures: null,
                CorrectionRule: null,
                DeclaredContextWindow: null));
}
