using System.Globalization;
using System.Text.Json;
using KyberDash.Application;
using KyberDash.Application.Tests.Fakes;
using KyberDash.Contracts.Repositories;
using KyberDash.Contracts.Services;
using KyberDash.Data.Collector;
using KyberDash.Domain;
using Xunit;

namespace KyberDash.Domain.Tests;

/// <summary>
/// Pricing, token-cache, context/schema/timeline analysis, outcome derivation, and finding detector
/// behaviour ported from the C# engine's parity suite.
/// </summary>
/// <remarks>
/// The domain has no pricing snapshot of its own: the rate tables ship with the data layer. These
/// tests install the published snapshot and fallback rates through
/// <see cref="CanonPricing.ConfigurePricingProvider"/> before asserting rates, so real model prices
/// are exercised rather than placeholders.
/// </remarks>
public class CanonAnalysisTests
{
    private static readonly object[] ListedSkillEntries = [new { name = "dormant-skill" }, new { name = "active-skill" }];
    private static readonly string[] ListedSkillNames = ["dormant-skill", "active-skill"];
    private static readonly string[] InvokedSkills = ["active-skill"];

    /// <summary>The pricing fixture is installed once per test process by <see cref="PricingFixture.EnsureInstalled"/>.</summary>
    public CanonAnalysisTests()
    {
    }

    [Fact]
    public void PricingAndTierEscalation_MatchTypeScriptSemantics()
    {
        PricingFixture.EnsureInstalled();
        Assert.Equal("claude-sonnet-4-6", CanonPricing.ResolveCanonicalModelId("anthropic--claude-4.6-sonnet"));
        Assert.Equal("claude-sonnet-4-5", CanonPricing.ResolveCanonicalModelId("cursor-auto"));
        Assert.Equal("claude-opus-4-6", CanonPricing.ResolveCanonicalModelId("openrouter/anthropic/claude-opus-4-6-20260101"));
        Assert.True(CanonPricing.IsBuiltInFlatRateModel("auto"));
        Assert.True(CanonPricing.IsBuiltInFlatRateModel("grok-composer-fast"));
        Assert.False(CanonPricing.IsBuiltInFlatRateModel("claude-sonnet-4-6"));
        ModelCosts? sonnetCosts = CanonPricing.GetModelCosts("claude-sonnet-4-6");
        Assert.NotNull(sonnetCosts);
        Assert.True(sonnetCosts.InputCostPerToken > 0);
        Assert.True(sonnetCosts.OutputCostPerToken > 0);

        double oneHourWriteCost = CanonPricing.CalculateCost(
            "claude-sonnet-4-6",
            inputTokens: 0,
            outputTokens: 0,
            cacheCreationTokens: 1_000,
            cacheReadTokens: 0,
            webSearchRequests: 0,
            fast: false,
            oneHourCacheCreationTokens: 1_000);
        double fiveMinWriteCost = CanonPricing.CalculateCost(
            "claude-sonnet-4-6",
            inputTokens: 0,
            outputTokens: 0,
            cacheCreationTokens: 1_000,
            cacheReadTokens: 0,
            webSearchRequests: 0,
            fast: false,
            oneHourCacheCreationTokens: 0);
        Assert.Equal(
            fiveMinWriteCost * CanonPricing.OneHourCacheWriteMultiplier,
            oneHourWriteCost,
            precision: 12);

        double lunaAtThreshold = CanonPricing.CalculateCost("gpt-6-luna", CanonPricing.Gpt6LunaPromptTokenThreshold, 1_000, 0, 0, 0);
        double lunaAboveThreshold = CanonPricing.CalculateCost("gpt-6-luna", CanonPricing.Gpt6LunaPromptTokenThreshold + 1, 1_000, 0, 0, 0);
        Assert.True(lunaAboveThreshold > lunaAtThreshold);

        double grokBelow = CanonPricing.CalculateCost("grok-4.6", CanonPricing.Grok46PromptTokenThreshold - 1, 1_000, 0, 0, 0);
        double grokAt = CanonPricing.CalculateCost("grok-4.6", CanonPricing.Grok46PromptTokenThreshold, 1_000, 0, 0, 0);
        Assert.True(grokAt > grokBelow);

        TokenUsage sampleTokens = new(FreshInput: 1_000, CacheRead: 500, CacheCreation: 200, Output: 300, ReportedInput: 1_700, ReportedOutput: 300);
        CostBlock harnessExisting = new(CostBasis.Harness, CostStatus.Priced, 0.42, "USD");
        Assert.Same(harnessExisting, CanonPricing.PriceTurn(sampleTokens, "claude-sonnet-4-6", "claude-code", harnessExisting));

        CostBlock copilotBase = CanonPricing.PriceTurn(sampleTokens, "gpt-6-luna", "copilot-vscode");
        Assert.Equal(CostBasis.Published, copilotBase.Basis);
        Assert.Equal(CostStatus.Priced, copilotBase.Status);
        Assert.Equal("USD", copilotBase.Currency);
        Assert.True((copilotBase.Value ?? 0) > 0);

        TokenUsage highTierTokens = new(
            FreshInput: CanonPricing.Gpt6LunaPromptTokenThreshold + 1,
            CacheRead: 0,
            CacheCreation: 0,
            Output: 1_000,
            ReportedInput: CanonPricing.Gpt6LunaPromptTokenThreshold + 1,
            ReportedOutput: 1_000);
        TokenUsage boundaryTokens = new(
            FreshInput: CanonPricing.Gpt6LunaPromptTokenThreshold,
            CacheRead: 0,
            CacheCreation: 0,
            Output: 1_000,
            ReportedInput: CanonPricing.Gpt6LunaPromptTokenThreshold,
            ReportedOutput: 1_000);
        CostBlock copilotAtLimit = CanonPricing.PriceTurn(boundaryTokens, "gpt-6-luna", "copilot-cli");
        CostBlock copilotAboveLimit = CanonPricing.PriceTurn(highTierTokens, "gpt-6-luna", "copilot-cli");
        Assert.True((copilotAboveLimit.Value ?? 0) > (copilotAtLimit.Value ?? 0) * 1.5);

        CostBlock claudePriced = CanonPricing.PriceTurn(sampleTokens, "claude-sonnet-4-6", "claude-code");
        Assert.Equal(CostBasis.Published, claudePriced.Basis);
        Assert.Equal(CostStatus.Priced, claudePriced.Status);
        Assert.Equal("USD", claudePriced.Currency);
        Assert.True((claudePriced.Value ?? 0) > 0);

        CostBlock flatNotBilled = CanonPricing.PriceTurn(sampleTokens, "auto", "claude-code");
        Assert.Equal(CostStatus.NotBilled, flatNotBilled.Status);

        CostBlock opencodeOutOfScope = CanonPricing.PriceTurn(sampleTokens, "claude-sonnet-4-6", "opencode");
        Assert.Equal(CostStatus.OutOfScope, opencodeOutOfScope.Status);

        CostTotalResult uniformSum = CanonPricing.SumCosts([claudePriced, new CostBlock(CostBasis.Published, CostStatus.Priced, 0.25, "USD")]);
        Assert.True(uniformSum.Ok);
        Assert.NotNull(uniformSum.Total);
        Assert.Equal((claudePriced.Value ?? 0) + 0.25, uniformSum.Total.Value ?? 0, precision: 10);

        CostTotalResult mixedBasisSum = CanonPricing.SumCosts([claudePriced, harnessExisting]);
        Assert.False(mixedBasisSum.Ok);
        Assert.NotNull(mixedBasisSum.Problem);
        Assert.Equal(CanonPricing.CostBasisMismatchCode, mixedBasisSum.Problem.Code);
        CostBasisMismatch? mismatch = CanonPricing.CostBasisMismatchOf([claudePriced, harnessExisting], mixedBasisSum);
        Assert.NotNull(mismatch);
        Assert.Equal(CanonPricing.CostBasisMismatchCode, mismatch.Code);
    }

    [Fact]
    public void ContextSchemaAndTimeline_MatchTypeScriptAnalyses()
    {
        List<ContextPart> rawParts =
        [
            new ContextPart("system_prompt", new string('s', 400)),
            new ContextPart("tool_definitions", new string('t', 200), Server: "github"),
            new ContextPart("tool_definitions", new string('u', 120), Server: null, Resident: false),
            new ContextPart("conversation_history", "hello <system-reminder>keep-this-rule</system-reminder> world"),
        ];

        IReadOnlyList<ContextPart> reduced = CanonContextAnalysis.ReduceContextParts(rawParts, CountWords);
        Assert.Equal(4, reduced.Count);
        Assert.All(reduced, static part => Assert.Equal(string.Empty, part.Text));
        Assert.False(reduced[2].Resident);
        Assert.True(reduced[3].InstructionTokens > 0);
        Assert.Equal(1, reduced[3].StrippedCount);

        List<ContextTurn> turns =
        [
            new ContextTurn(rawParts, InputTokens: 500, FreshInput: 0),
            new ContextTurn(
                [
                    new ContextPart("system_prompt", string.Empty, Tokens: 100),
                    new ContextPart("conversation_history", "msg <system-reminder>rule</system-reminder>"),
                ],
                InputTokens: 2_500,
                FreshInput: 1_500),
            new ContextTurn(
                [
                    new ContextPart("system_prompt", string.Empty, Tokens: 100),
                ],
                InputTokens: 900,
                FreshInput: 4_000),
        ];

        ContextAnalysis context = CanonContextAnalysis.AnalyzeContext(
            turns,
            new ContextCountOptions(ContextLimit: 10_000, ContextLimitSource: "telemetry"),
            CountWords);
        Assert.True(context.Measurable);
        Assert.Equal(3, context.TurnCount);
        Assert.True(context.Turns[0].ToolDefinitionsByServer.ContainsKey("github"));
        Assert.Equal(1, context.Turns[0].StrippedInstructionBlocks.Count);
        Assert.Equal(2, context.Turns[1].Index);
        Assert.NotNull(context.Turns[1].FreshInputJump);
        Assert.True(double.IsPositiveInfinity(context.Turns[1].FreshInputJump!.Factor));
        Assert.Contains(2, context.FlaggedTurns);
        Assert.Equal(3, context.Turns[2].Index);
        Assert.Contains(3, context.FlaggedTurns);

        Dictionary<string, JsonElement> unmeasuredAllKeys = new(StringComparer.Ordinal);
        foreach (string contentKey in CanonValidation.CanonicalContentKeys)
        {
            unmeasuredAllKeys[contentKey] = Element(new { availability = "not_measurable" });
        }

        ContextAnalysis notMeasurable = CanonContextAnalysis.AnalyzeContext(
            turns,
            new ContextCountOptions(
                ContextLimit: 10_000,
                Measurability: unmeasuredAllKeys),
            CountWords);
        Assert.False(notMeasurable.Measurable);
        Assert.Equal("declared_not_measurable", notMeasurable.Reason);

        SchemaCostAnalysis schema = CanonContextAnalysis.RankSchemas(
            [
                new ToolDefinition("dormant_alpha", Server: "mcp-one", Tokens: 300),
                new ToolDefinition("used_beta", Server: "mcp-one", Tokens: 100),
                new ToolDefinition("dormant_gamma", Server: "mcp-two", Tokens: 200, TurnsResident: 2),
            ],
            turns: 4,
            invocations: ["used_beta"],
            rates: new SchemaCostRates(CacheReadRate: 0.30, FreshInputRate: 3.00, Currency: "USD"));
        Assert.True(schema.Measurable);
        Assert.Equal("dormant_alpha", schema.Ranked[0].Name);
        Assert.Equal(1_200, schema.Ranked[0].Cost);
        Assert.Equal(2, schema.NeverInvoked.Count);
        Assert.Equal(1_600, schema.ByServer["mcp-one"]);
        Assert.Equal(400, schema.ByServer["mcp-two"]);
        Assert.NotNull(schema.UnusedRange);
        Assert.Equal(1_600, schema.UnusedRange.TokenResidencies);
        Assert.True(schema.UnusedRange.Ceiling > schema.UnusedRange.Floor);

        CanonicalRecord rootSpan = MakeLlmRecord(
            "root-1",
            "sess-tl",
            "claude-code",
            "2026-11-14T23:00:00.000Z",
            100,
            20,
            cost: new CostBlock(CostBasis.Published, CostStatus.Priced, 0.10, "USD"));
        CanonicalRecord subagentSpan = rootSpan with
        {
            SpanId = "sub-1",
            ParentSpanId = "root-1",
            Name = "invoke_agent subagent",
            Timestamp = "2026-11-14T23:00:01.000Z",
            DurationMs = 500,
            Raw = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
            {
                ["gen_ai.agent.name"] = Element("subagent-worker"),
            },
        };
        CanonicalRecord orphanSpan = rootSpan with
        {
            SpanId = "orphan-1",
            ParentSpanId = "missing-parent",
            Name = "orphan.tool",
            Timestamp = "2026-11-14T23:00:02.000Z",
        };
        CanonicalRecord auxiliarySpan = rootSpan with
        {
            SpanId = "aux-1",
            ParentSpanId = null,
            Name = "title",
            Timestamp = "2026-11-14T23:00:03.000Z",
            Cost = new CostBlock(CostBasis.Published, CostStatus.Priced, 0.02, "USD"),
        };

        TimelineNode root = CanonContextAnalysis.BuildTimeline([rootSpan, subagentSpan, orphanSpan, auxiliarySpan]);
        Assert.Equal(CanonContextAnalysis.SessionRootId, root.SpanId);
        Assert.Contains(root.Children, static child => child.SpanId == "root-1");
        Assert.Contains(root.Children, static child => child.SpanId == CanonContextAnalysis.AuxiliaryGroupId);
        Assert.Contains(root.Children, static child => child.SpanId.StartsWith("(orphan:", StringComparison.Ordinal));

        IReadOnlyList<SubagentSession> subagents = CanonContextAnalysis.SubagentSessions(root);
        SubagentSession singleSubagent = Assert.Single(subagents);
        Assert.Equal("sub-1", singleSubagent.SpanId);
        Assert.Equal("root-1", singleSubagent.ParentSpanId);

        CostBlock auxiliarySpend = CanonContextAnalysis.AuxiliarySpend(root);
        Assert.Equal(CostStatus.Priced, auxiliarySpend.Status);
        Assert.Equal(0.02, auxiliarySpend.Value ?? 0, precision: 10);
    }

    /// <summary>
    /// A level wider than a page keeps the total sibling order the timeline section pages by:
    /// start offset, then span id, with no duplicate and none dropped, and the synthetic
    /// containers — <c>(auxiliary)</c> and the <c>(orphan:...)</c> groups — are addressable by
    /// their exact ids so a parent request can name them.
    /// </summary>
    [Fact]
    public void TimelineSiblingsKeepATotalOrderAcrossAWideLevelWithSyntheticContainers()
    {
        CanonicalRecord seed = MakeLlmRecord(
            "seed-0",
            "sess-wide",
            "claude-code",
            "2026-11-14T23:00:00.000Z",
            100,
            20);

        List<CanonicalRecord> records = [];
        for (int index = 0; index < 30; index++)
        {
            // Same start offset on every primary root, so the order falls to the span id tiebreak.
            records.Add(seed with { SpanId = $"root-{index:D3}" });
        }

        for (int index = 0; index < 30; index++)
        {
            records.Add(seed with
            {
                SpanId = $"aux-{index:D3}",
                ParentSpanId = null,
                Name = $"chat.title {index}",
            });
        }

        for (int index = 0; index < 2; index++)
        {
            records.Add(seed with
            {
                SpanId = $"lost-{index}",
                ParentSpanId = "missing-parent",
                Name = "orphan.tool",
            });
        }

        TimelineNode root = CanonContextAnalysis.BuildTimeline(records);

        // The top level holds every container and root, ordered by start offset then span id.
        List<string> topLevel = [.. root.Children.Select(static node => node.SpanId)];
        Assert.Equal(32, topLevel.Count);
        Assert.Equal(topLevel.Count, topLevel.Distinct(StringComparer.Ordinal).Count());
        List<string> ordered = [.. topLevel.Order(StringComparer.Ordinal)];
        Assert.Equal(ordered, topLevel);

        TimelineNode auxiliary = Assert.Single(root.Children, static node => node.SpanId == "(auxiliary)");
        Assert.Equal(30, auxiliary.Children.Count);
        Assert.Equal(
            [.. Enumerable.Range(0, 30).Select(index => $"aux-{index:D3}")],
            auxiliary.Children.Select(static node => node.SpanId));

        TimelineNode orphan = Assert.Single(root.Children, static node => node.SpanId == "(orphan:claude-code:orphan.tool)");
        Assert.Equal(["lost-0", "lost-1"], orphan.Children.Select(static node => node.SpanId));
    }

    [Fact]
    public void OutcomeAndFindingDetectors_RankAndLintPerD5D6D8()
    {
        CorrectionDetectionResult matchedCorrection = CanonFindings.DetectUserCorrection("No, wait, revert that and do it properly.");
        Assert.True(matchedCorrection.Matched);

        D8LintResult invalidDelete = CanonFindings.LintRecommendationD8("Delete the unused MCP tools from config.");
        Assert.False(invalidDelete.Valid);
        Assert.NotEmpty(invalidDelete.Errors);

        D8LintResult validRelocate = CanonFindings.LintRecommendationD8("Relocate dormant tool schemas to on-demand deferred loading.");
        Assert.True(validRelocate.Valid);
        Assert.Empty(validRelocate.Errors);

        CanonicalRecord unmeasuredTurn = MakeLlmRecord("span-unmeasured", "sess-f", "claude-code", "2026-11-14T23:00:00.000Z", 500, 100);
        OutcomeBlock unmeasuredOutcome = CanonFindings.DeriveOutcome([unmeasuredTurn]);
        Assert.Equal("not_measurable", unmeasuredOutcome.Status);

        CanonicalRecord cleanTurn = MakeLlmRecord(
            "span-ok",
            "sess-f",
            "claude-code",
            "2026-11-14T23:00:00.000Z",
            500,
            100,
            raw: new Dictionary<string, JsonElement>(StringComparer.Ordinal)
            {
                ["type"] = Element("task_complete"),
            });
        OutcomeBlock successOutcome = CanonFindings.DeriveOutcome([cleanTurn]);
        Assert.Equal("success", successOutcome.Status);

        CanonicalRecord errorTurn = MakeLlmRecord(
            "span-err",
            "sess-f",
            "claude-code",
            "2026-11-14T23:00:00.000Z",
            500,
            100,
            raw: new Dictionary<string, JsonElement>(StringComparer.Ordinal)
            {
                ["type"] = Element("task_failed"),
            }) with
        { Status = "error" };
        OutcomeBlock failureOutcome = CanonFindings.DeriveOutcome([errorTurn]);
        Assert.Equal("failure", failureOutcome.Status);

        OutcomePopulationReport population = CanonFindings.CalculateOutcomePopulationRate([unmeasuredOutcome, successOutcome, failureOutcome]);
        Assert.Equal(3, population.TotalRuns);
        Assert.Equal(2, population.MeasurableCount);
        Assert.Equal(1, population.UnmeasurableCount);
        Assert.Equal(2.0 / 3.0, population.PopulationRate, precision: 10);

        string prefixA = new string('p', 600) + "-tail-a";
        string prefixB = new string('q', 600) + "-tail-b";
        string largeResultText = new('r', 12_000);
        List<CanonicalRecord> detectorRecords =
        [
            MakeLlmRecord(
                "llm-1",
                "sess-find",
                "claude-code",
                "2026-11-14T23:00:00.000Z",
                freshInput: 45_000,
                output: 400,
                cacheRead: 2_000,
                parts:
                [
                    new ContentPart("system_prompt", prefixA),
                ],
                raw: new Dictionary<string, JsonElement>(StringComparer.Ordinal)
                {
                    ["gen_ai.request.max_context_tokens"] = Element(100_000),
                    ["kyber.skills.listed"] = Element(new { isInitial = true, skills = ListedSkillEntries }),
                    ["kyber.skills.invoked"] = Element(InvokedSkills),
                }),
            new CanonicalRecord
            {
                SpanId = "tool-dup-1",
                TraceId = "trace-find",
                ParentSpanId = "llm-1",
                Source = "claude-code",
                Harness = "claude-code",
                SessionId = "sess-find",
                Name = "read_file",
                Op = "tool.invoke",
                Kind = "internal",
                Timestamp = "2026-11-14T23:00:01.000Z",
                DurationMs = 20,
                Status = "ok",
                Tokens = new TokenUsage(0, 0, 0, 0, 0, 0),
                Content = new Dictionary<string, string>(StringComparer.Ordinal),
                Parts =
                [
                    new ContentPart("tool_result_content", largeResultText, Tokens: 4_000),
                ],
                Cost = new CostBlock(CostBasis.Published, CostStatus.OutOfScope),
                Raw = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
                {
                    ["arguments"] = Element(new { path = "/tmp/same.txt" }),
                    ["gen_ai.tool.result_bytes"] = Element(12_000),
                },
            },
            new CanonicalRecord
            {
                SpanId = "tool-dup-2",
                TraceId = "trace-find",
                ParentSpanId = "llm-1",
                Source = "claude-code",
                Harness = "claude-code",
                SessionId = "sess-find",
                Name = "read_file",
                Op = "tool.invoke",
                Kind = "internal",
                Timestamp = "2026-11-14T23:00:02.000Z",
                DurationMs = 20,
                Status = "ok",
                Tokens = new TokenUsage(0, 0, 0, 0, 0, 0),
                Content = new Dictionary<string, string>(StringComparer.Ordinal),
                Parts =
                [
                    new ContentPart("tool_result_content", "ok", Tokens: 200),
                ],
                Cost = new CostBlock(CostBasis.Published, CostStatus.OutOfScope),
                Raw = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
                {
                    ["arguments"] = Element(new { path = "/tmp/same.txt" }),
                },
            },
            MakeLlmRecord(
                "llm-2",
                "sess-find",
                "claude-code",
                "2026-11-14T23:00:03.000Z",
                freshInput: 85_000,
                output: 500,
                cacheRead: 0,
                parts:
                [
                    new ContentPart("system_prompt", prefixB),
                ],
                raw: new Dictionary<string, JsonElement>(StringComparer.Ordinal)
                {
                    ["gen_ai.request.max_context_tokens"] = Element(100_000),
                    ["kyber.skills.listed"] = Element(new { isInitial = true, skills = ListedSkillEntries }),
                    ["kyber.skills.invoked"] = Element(InvokedSkills),
                }),
            MakeLlmRecord(
                "subagent-heavy",
                "sess-find",
                "claude-code",
                "2026-11-14T23:00:04.000Z",
                freshInput: 300_000,
                output: 5_000,
                raw: new Dictionary<string, JsonElement>(StringComparer.Ordinal)
                {
                    ["is_subagent"] = Element(true),
                }),
        ];

        IReadOnlyList<Finding> findings = CanonFindings.DetectFindings(new DetectFindingsInput(
            RunId: "run-parity",
            SessionId: "sess-find",
            Records: detectorRecords,
            ContextLimit: 100_000,
            DormantToolSchema: new DormantToolSchemaInput(
                RunId: "run-parity",
                SessionId: "sess-find",
                ToolDefinitions:
                [
                    new ToolDefinitionSupply("never_called_tool", Tokens: 600, Server: "mcp-extra"),
                    new ToolDefinitionSupply("read_file", Tokens: 200, Server: "fs"),
                ],
                TurnsCount: 3,
                Invocations: ["read_file"])));

        Assert.NotEmpty(findings);
        HashSet<string> firedDetectors = new(findings.Select(static finding => finding.DetectorId), StringComparer.Ordinal);
        Assert.Contains("dormant-tool-schema", firedDetectors);
        Assert.Contains("duplicate-tool-call", firedDetectors);
        Assert.Contains("oversized-tool-result", firedDetectors);
        Assert.Contains("prefix-cache-break", firedDetectors);
        Assert.Contains("compaction-hazard", firedDetectors);
        Assert.Contains("unbounded-delegation", firedDetectors);
        Assert.Contains("inactive-skill-reference", firedDetectors);

        foreach (Finding finding in findings)
        {
            Assert.NotEmpty(finding.EvidenceLinks);
            Assert.Null(finding.ErrorBar);

            Assert.False(string.IsNullOrWhiteSpace(finding.OutcomeRiskCaveat));
            D8LintResult lint = CanonFindings.LintRecommendationD8(finding.Recommendation);
            Assert.True(lint.Valid, $"D8 lint failed for {finding.DetectorId}: {string.Join("; ", lint.Errors)}");
        }

        IReadOnlyList<Finding> measuredSkillFindings = CanonFindings.DetectInactiveSkillReference(
            new InactiveSkillReferenceInput(
                SessionId: "sess-find",
                Records: detectorRecords,
                ReferencedSkills: ListedSkillNames,
                ExecutedSkills: InvokedSkills,
                SkillTokens: new Dictionary<string, long>(StringComparer.Ordinal)
                {
                    ["dormant-skill"] = 350,
                }));
        Finding measuredSkill = Assert.Single(measuredSkillFindings);
        Assert.Null(measuredSkill.ErrorBar);
        Assert.Equal("inferred", measuredSkill.MeasurementClass);

        Finding skillTied = findings.First(static finding => finding.DetectorId == "inactive-skill-reference") with
        {
            RankScore = 500.0,
            Confidence = "measured",
            EstimatedWasteTokens = 5_000,
        };
        Finding duplicateTied = findings.First(static finding => finding.DetectorId == "duplicate-tool-call") with
        {
            RankScore = 500.0,
            Confidence = "calibrated_statistical",
            EstimatedWasteTokens = 100,
        };
        Finding higherScore = duplicateTied with
        {
            Id = "higher-score",
            RankScore = 900.0,
        };

        IReadOnlyList<Finding> ranked = CanonFindings.RankFindings([skillTied, duplicateTied, higherScore]);
        Assert.Equal("higher-score", ranked[0].Id);
        Assert.Equal("duplicate-tool-call", ranked[1].DetectorId);
        Assert.Equal("inactive-skill-reference", ranked[2].DetectorId);
    }

    [Fact]
    public void SessionProjection_RebuildsDirtySessionsPrunesStaleOnesAndPersistsFindings()
    {
        InMemoryRelationalStore store = new();
        CanonSessionProjectorEngine projector = new(store, store, store, new FakePricingRepository(), new FakeTextTokenizer());

        Assert.True(projector.IsProjectionCurrent());

        WriteSession(
            store,
            "sess-a",
            "claude-code",
            [
                Turn(
                    "span-a-1",
                    "sess-a",
                    "claude-code",
                    inputTokens: 400,
                    cacheRead: 100,
                    outputTokens: 60,
                    parts:
                    [
                        TextPart(0, ContentBucket.SystemPrompt, "You are helpful."),
                        TextPart(1, ContentBucket.UserPrompt, "Fix the bug"),
                    ],
                    model: "claude-sonnet-4-6",
                    declaredContextWindow: 200_000),
            ]);
        WriteSession(
            store,
            "sess-b",
            "codex",
            [
                Turn(
                    "span-b-1",
                    "sess-b",
                    "codex",
                    minuteOffset: 1,
                    inputTokens: 1_500,
                    outputTokens: 120,
                    parts: [TextPart(0, ContentBucket.UserPrompt, "Build feature B")],
                    model: "gpt-5.3-codex",
                    declaredContextWindow: 200_000),
            ]);
        store.SetWriteMarker(1);
        Assert.False(projector.IsProjectionCurrent());

        ProjectionPassReport firstPass = projector.ProjectIncremental();
        Assert.Equal(2, firstPass.Built);
        Assert.Equal(0, firstPass.Pruned);
        Assert.Contains("sess-a", firstPass.RebuiltKeys);
        Assert.Contains("sess-b", firstPass.RebuiltKeys);
        Assert.True(projector.IsProjectionCurrent());

        StoredSessionProjection? sessionA = ((IProjectionStore)store).GetSessionProjection("sess-a");
        Assert.NotNull(sessionA);
        Assert.Equal(1, sessionA.SpanCount);
        Assert.NotNull(sessionA.Context);
        StoredSessionContextTurn contextTurn = Assert.Single(sessionA.Context.Turns, static turn => turn.Position == 0);
        Assert.Equal(500, contextTurn.InputTokens);

        WriteSession(
            store,
            "sess-b",
            "codex",
            [
                Turn(
                    "span-b-tool",
                    "sess-b",
                    "codex",
                    minuteOffset: 2,
                    parentSpanId: "span-b-1",
                    name: "search_docs",
                    op: RecordOp.ToolInvoke,
                    parts: [new CollectedContentPart(0, ContentBucket.ToolResultContent, new string('z', 15_000), null, 3_750, null, null, null, null)],
                    model: "gpt-5.3-codex",
                    declaredContextWindow: 200_000),
            ]);
        store.SetWriteMarker(2);
        store.SetProjectionDirtyState(new DirtyProjectionState(FromSeq: 1, ToSeq: 2, Keys: ["sess-b"], IsWildcard: false));
        Assert.False(projector.IsProjectionCurrent());

        ProjectionPassReport secondPass = projector.ProjectIncremental();
        Assert.Equal(1, secondPass.Built);
        Assert.Equal(1, secondPass.Skipped);
        Assert.True(secondPass.Findings >= 1);

        IReadOnlyList<StoredFinding> sessionBFindings = ((IProjectionStore)store).ListFindings(sessionId: "sess-b");
        Assert.Contains(sessionBFindings, static finding => finding.DetectorId == "oversized-tool-result");

        // A stale projection with no live records is pruned on the next pass.
        WriteSession(store, "sess-gone", "codex", []);
        ((IProjectionStore)store).ReplaceSessionProjection(new StoredSessionProjection(
            SessionId: "sess-gone",
            Label: "gone",
            IsSubagent: false,
            SpanCount: 1,
            StartedAtUtc: "2026-11-01T00:00:00.000Z",
            EndedAtUtc: "2026-11-01T00:05:00.000Z",
            ParentSessionId: null,
            AgentName: null,
            IncludedInParent: false,
            RequestSpanIds: [],
            Turns: []));
        store.SetWriteMarker(3);

        ProjectionPassReport thirdPass = projector.ProjectIncremental();
        Assert.Equal(1, thirdPass.Pruned);
        Assert.Null(((IProjectionStore)store).GetSessionProjection("sess-gone"));
    }

    [Fact]
    public void RunAndHarnessRollups_GroupSessionsPerHarnessAndStoreExecutions()
    {
        InMemoryRelationalStore store = new();
        FakePricingRepository pricing = new();

        WriteSession(store, "s1", "claude-code", [Turn("span-s1", "s1", "claude-code", inputTokens: 100, outputTokens: 10)]);
        WriteSession(store, "s2", "claude-code", [Turn("span-s2", "s2", "claude-code", minuteOffset: 120, inputTokens: 100, outputTokens: 10)]);
        WriteSession(store, "s3", "codex", [Turn("span-s3", "s3", "codex", minuteOffset: 1_500, inputTokens: 100, outputTokens: 10)]);

        CanonSessionProjectorEngine projector = new(store, store, store, pricing, new FakeTextTokenizer());
        ProjectionPassReport pass = projector.ProjectIncremental();
        Assert.Equal(3, pass.Built);

        CanonRollupsEngine rollups = new(store, store, pricing);
        IReadOnlyList<HarnessRollupRow> rows = rollups.ProjectRunsAndHarnesses();
        Assert.Equal(2, rows.Count);
        HarnessRollupRow claude = Assert.Single(rows, static row => row.Harness == "claude-code");
        Assert.Equal(2, claude.SampleCount);
        HarnessRollupRow codex = Assert.Single(rows, static row => row.Harness == "codex");
        Assert.Equal(1, codex.SampleCount);

        IReadOnlyList<StoredRun> runs = ((IProjectionStore)store).ListRuns();
        Assert.Equal(3, runs.Count);
        Assert.Contains(runs, static run => run.RunId == "derived:claude-code:s1" && run.ExecutionCount == 1);
        Assert.Contains(runs, static run => run.RunId == "derived:claude-code:s2" && run.ExecutionCount == 1);
        Assert.Contains(runs, static run => run.RunId == "derived:codex:s3" && run.ExecutionCount == 1);

        IReadOnlyList<StoredExecution> executions = ((IProjectionStore)store).ListExecutionsForRun("derived:claude-code:s1");
        StoredExecution execution = Assert.Single(executions);
        Assert.True(execution.IsRoot);

        Assert.Equal(2, CanonRollupsEngine.ComputeMedian([1.0, 3.0]));
        Assert.Equal(3.0, CanonRollupsEngine.ComputeP95([1.0, 2.0, 3.0]));
    }

    private static void WriteSession(
        InMemoryRelationalStore store,
        string sessionKey,
        string harnessId,
        IReadOnlyList<CollectedRecord> records) =>
        store.WriteBatch(new CollectedBatch(
            Source: new CollectedSource(harnessId, SourceKind.Otlp, "otlp:" + harnessId),
            Sessions:
            [
                new CollectedSession(
                    SessionKey: sessionKey,
                    HarnessId: harnessId,
                    NativeSessionId: sessionKey,
                    Workspace: null,
                    Title: null,
                    ReportedUsage: []),
            ],
            Records: records,
            Problems: []));

    private static CollectedRecord Turn(
        string spanId,
        string sessionKey,
        string harnessId,
        int minuteOffset = 0,
        long inputTokens = 0,
        long cacheRead = 0,
        long outputTokens = 0,
        string? parentSpanId = null,
        string name = "chat.turn",
        RecordOp op = RecordOp.LlmInvoke,
        string? model = "claude-sonnet-4-6",
        IReadOnlyList<CollectedContentPart>? parts = null,
        long? declaredContextWindow = null) =>
        new CollectedRecord(
            RecordId: spanId,
            HarnessId: harnessId,
            Op: op,
            Kind: RecordKind.Client,
            Name: name,
            StartedAt: TurnBase.AddMinutes(minuteOffset),
            Status: RecordStatus.Ok,
            SessionKey: sessionKey,
            ParentRecordId: parentSpanId,
            TraceId: null,
            Model: model,
            EndedAt: null,
            ReportedDurationMs: 1_000,
            Tokens:
            [
                new CollectedTokenCount(TokenClass.Input, inputTokens),
                new CollectedTokenCount(TokenClass.CacheRead, cacheRead),
                new CollectedTokenCount(TokenClass.CacheWrite, 0),
                new CollectedTokenCount(TokenClass.Output, outputTokens),
            ],
            ReportedCost: null,
            Parts: parts ?? [],
            Tool: null,
            Agent: null,
            SkillsInvoked: [],
            AgentInstanceId: null,
            ToolDefinitions: [],
            Signals: new CollectedRecordSignals(ErrorMessage: null, EditFailures: null, CorrectionRule: null, DeclaredContextWindow: declaredContextWindow),
            ReportedInputTokens: inputTokens + cacheRead,
            ReportedOutputTokens: outputTokens);

    private static CollectedContentPart TextPart(int position, ContentBucket bucket, string text) =>
        new(
            Position: position,
            Bucket: bucket,
            PlainText: text,
            Withheld: null,
            ReportedTokens: null,
            Server: null,
            Role: null,
            Resident: null,
            OriginalUtf8BytesWhenTruncated: null);

    private static readonly DateTimeOffset TurnBase = new(2026, 11, 14, 23, 0, 0, TimeSpan.Zero);

    private static CanonicalRecord MakeLlmRecord(
        string spanId,
        string sessionId,
        string harness,
        string timestamp,
        long freshInput,
        long output,
        long cacheRead = 0,
        long cacheCreation = 0,
        IReadOnlyList<ContentPart>? parts = null,
        IReadOnlyDictionary<string, JsonElement>? raw = null,
        CostBlock? cost = null)
    {
        TokenUsage tokens = new(
            FreshInput: freshInput,
            CacheRead: cacheRead,
            CacheCreation: cacheCreation,
            Output: output,
            ReportedInput: freshInput + cacheRead + cacheCreation,
            ReportedOutput: output);

        return new CanonicalRecord
        {
            SpanId = spanId,
            TraceId = $"trace-{sessionId}",
            ParentSpanId = null,
            Source = harness,
            Harness = harness,
            SessionId = sessionId,
            Name = "llm.invoke",
            Op = "llm.invoke",
            Kind = "client",
            Timestamp = timestamp,
            DurationMs = 250,
            Status = "ok",
            Tokens = tokens,
            Content = parts is null
                ? new Dictionary<string, string>(StringComparer.Ordinal)
                : CanonValidation.ContentFromParts(parts),
            Parts = parts,
            Cost = cost ?? new CostBlock(CostBasis.Published, CostStatus.NoRate),
            Raw = raw,
        };
    }

    private static JsonElement Element(object value) => JsonSerializer.SerializeToElement(value);

    /// <summary>Stand-in counter: one count per whitespace-separated word, never characters / 4.</summary>
    private static long CountWords(string text, string? model) =>
        text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).LongLength;
}
