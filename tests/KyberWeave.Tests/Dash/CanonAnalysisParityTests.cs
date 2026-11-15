using System.Globalization;
using System.Text.Json;
using KyberWeave.Core.Dash;
using Xunit;

namespace KyberWeave.Tests.Dash;

/// <summary>
/// Verifies pricing and tier escalation, token-count cache memoization, context/schema/timeline
/// analyses, empirical outcomes, D5/D6/D8 finding detectors, and incremental session projection
/// parity between Slice 3 C# modules and <c>dash/src/canon/</c> + <c>dash/src/analysis/</c>.
/// </summary>
public sealed class CanonAnalysisParityTests
{
    private static readonly object[] ListedSkillEntries = [new { name = "dormant-skill" }, new { name = "active-skill" }];
    private static readonly string[] ListedSkillNames = ["dormant-skill", "active-skill"];
    private static readonly string[] InvokedSkills = ["active-skill"];

    [Fact]
    public void PricingAndTierEscalation_MatchesTypeScriptSemantics()
    {
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

        // GPT-6 Luna escalates strictly above Gpt6LunaPromptTokenThreshold (272k).
        double lunaAtThreshold = CanonPricing.CalculateCost("gpt-6-luna", CanonPricing.Gpt6LunaPromptTokenThreshold, 1_000, 0, 0, 0);
        double lunaAboveThreshold = CanonPricing.CalculateCost("gpt-6-luna", CanonPricing.Gpt6LunaPromptTokenThreshold + 1, 1_000, 0, 0, 0);
        Assert.True(lunaAboveThreshold > lunaAtThreshold);

        // Grok 4.6 escalates inclusively at Grok46PromptTokenThreshold (200k).
        double grokBelow = CanonPricing.CalculateCost("grok-4.6", CanonPricing.Grok46PromptTokenThreshold - 1, 1_000, 0, 0, 0);
        double grokAt = CanonPricing.CalculateCost("grok-4.6", CanonPricing.Grok46PromptTokenThreshold, 1_000, 0, 0, 0);
        Assert.True(grokAt > grokBelow);

        // PriceTurn preserves harness basis, prices Copilot credits, published APIs, and out-of-scope cleanly.
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

        // SumCosts + CostBasisMismatchOf reject mixed billing bases and mixed currencies.
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

        CostTotalResult mixedCurrencySum = CanonPricing.SumCosts([
            new CostBlock(CostBasis.Published, CostStatus.Priced, 1.0, "USD"),
            new CostBlock(CostBasis.Published, CostStatus.Priced, 2.0, "EUR"),
        ]);
        Assert.False(mixedCurrencySum.Ok);
        Assert.NotNull(mixedCurrencySum.Problem);
        Assert.Equal(CanonPricing.CostCurrencyMismatchCode, mixedCurrencySum.Problem.Code);

        // ContextLimitOf: telemetry wins over declared, Antigravity-only without telemetry yields absent, otherwise default.
        CanonicalRecord telemetryRecord = MakeLlmRecord(
            "span-ctx-tel",
            "sess-ctx",
            "claude-code",
            "2026-11-14T23:00:00.000Z",
            100,
            50,
            raw: new Dictionary<string, JsonElement>(StringComparer.Ordinal)
            {
                ["contextWindow"] = Elem(256_000),
                [CanonPricing.DeclaredContextLimitKey] = Elem(128_000),
            });
        ContextWindowResult telemetryWindow = CanonPricing.ContextLimitOf([telemetryRecord]);
        Assert.Equal(256_000, telemetryWindow.ContextLimit);
        Assert.Equal("reported", telemetryWindow.ContextLimitSource);

        CanonicalRecord antigravityRecord = MakeLlmRecord(
            "span-ctx-agy",
            "sess-agy",
            "antigravity",
            "2026-11-14T23:00:00.000Z",
            100,
            50,
            raw: new Dictionary<string, JsonElement>(StringComparer.Ordinal)
            {
                ["gen_ai.agent.name"] = Elem("antigravity"),
            });
        ContextWindowResult agyWindow = CanonPricing.ContextLimitOf([antigravityRecord]);
        Assert.Equal(0, agyWindow.ContextLimit);
        Assert.Equal("absent", agyWindow.ContextLimitSource);
        Assert.True(CanonPricing.IsUnnamedContextWindow(agyWindow.ContextLimitSource));

        CanonicalRecord defaultRecord = MakeLlmRecord("span-ctx-def", "sess-def", "claude-code", "2026-11-14T23:00:00.000Z", 100, 50);
        ContextWindowResult defaultWindow = CanonPricing.ContextLimitOf([defaultRecord]);
        Assert.True(defaultWindow.ContextLimit > 0);
        Assert.Equal("default", defaultWindow.ContextLimitSource);
        Assert.True(CanonPricing.IsUnnamedContextWindow(defaultWindow.ContextLimitSource));

        Assert.Equal(
            "Refactor Auth",
            CanonPricing.FormatSessionTitle(new SessionTitleInput(TaskSummary: "Refactor Auth", Harness: "claude-code", SessionId: "s-1")));
        Assert.Equal(
            "OpenCode session",
            CanonPricing.FormatSessionTitle(new SessionTitleInput(Harness: "opencode", SessionId: "s-2")));
    }

    [Fact]
    public void TokenCache_MemoizesAndPersistsAboveMinLengthThreshold()
    {
        Assert.Equal(512, CanonTokenCache.MinCachedTextLength);
        using TempDirectory directory = new();
        CanonStore store = new(Path.Combine(directory.Path, "canon.db"));

        int delegateCalls = 0;
        long CountingApprox(string text)
        {
            delegateCalls++;
            return CanonTokenCache.ApproximateO200kBase(text);
        }

        CachedTokenCounter counter = CanonTokenCache.CreateCachedCounter(store, "test-tokenizer", CountingApprox);

        string shortText = new('x', CanonTokenCache.MinCachedTextLength - 1);
        long shortCount1 = counter.Count(shortText);
        long shortCount2 = counter.Count(shortText);
        Assert.Equal(shortCount1, shortCount2);
        Assert.Equal(2, delegateCalls);
        Assert.Equal(2, counter.Stats().Uncached);
        Assert.Null(store.GetCachedTokenCount(CanonTokenCache.CacheKey(shortText, "test-tokenizer")));

        string longText = new('a', CanonTokenCache.MinCachedTextLength);
        string expectedKey = CanonTokenCache.CacheKey(longText, "test-tokenizer");
        Assert.StartsWith("test-tokenizer:", expectedKey, StringComparison.Ordinal);

        long computed = counter.Count(longText);
        long memoHit = counter.Count(longText);
        Assert.Equal(CanonTokenCache.ApproximateO200kBase(longText), computed);
        Assert.Equal(computed, memoHit);
        Assert.Equal(3, delegateCalls);
        Assert.Equal(1, counter.Stats().Misses);
        Assert.Equal(1, counter.Stats().Hits);

        counter.Flush();
        Assert.Equal(computed, store.GetCachedTokenCount(expectedKey));

        int secondDelegateCalls = 0;
        CachedTokenCounter persistedReader = CanonTokenCache.CreateCachedCounter(
            store,
            "test-tokenizer",
            text =>
            {
                secondDelegateCalls++;
                return CanonTokenCache.ApproximateO200kBase(text);
            });
        long sqliteHit = persistedReader.Count(longText);
        Assert.Equal(computed, sqliteHit);
        Assert.Equal(0, secondDelegateCalls);
        Assert.Equal(1, persistedReader.Stats().Hits);
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

        IReadOnlyList<ContextPart> reduced = CanonContextAnalysis.ReduceContextParts(rawParts);
        Assert.Equal(4, reduced.Count);
        Assert.All(reduced, static p => Assert.Equal(string.Empty, p.Text));
        Assert.Equal(false, reduced[2].Resident);
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
            new ContextCountOptions(ContextLimit: 10_000, ContextLimitSource: "telemetry"));
        Assert.True(context.Measurable);
        Assert.Equal(3, context.TurnCount);
        Assert.True(context.Turns[0].ToolDefinitionsByServer.ContainsKey("github"));
        Assert.Equal(1, context.Turns[0].StrippedInstructionBlocks.Count);
        // Turn index 2 (1-based) jumps from FreshInput = 0 -> 1500 (>= DefaultZeroPreviousFloor): Infinity jump factor & flagged.
        Assert.Equal(2, context.Turns[1].Index);
        Assert.NotNull(context.Turns[1].FreshInputJump);
        Assert.True(double.IsPositiveInfinity(context.Turns[1].FreshInputJump!.Factor));
        Assert.Contains(2, context.FlaggedTurns);
        // Turn index 3 (1-based) jumps 1500 -> 4000 (2.667x >= 2.0): also flagged.
        Assert.Equal(3, context.Turns[2].Index);
        Assert.Contains(3, context.FlaggedTurns);

        Dictionary<string, JsonElement> unmeasuredAllKeys = new(StringComparer.Ordinal);
        foreach (string contentKey in CanonValidation.CanonicalContentKeys)
            unmeasuredAllKeys[contentKey] = Elem(new { availability = "not_measurable" });

        ContextAnalysis notMeasurable = CanonContextAnalysis.AnalyzeContext(
            turns,
            new ContextCountOptions(
                ContextLimit: 10_000,
                Measurability: unmeasuredAllKeys));
        Assert.False(notMeasurable.Measurable);
        Assert.Equal("declared_not_measurable", notMeasurable.Reason);

        // Schema cost ranking
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

        // Timeline building, orphan grouping, subagent extraction, and auxiliary spend
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
                ["gen_ai.agent.name"] = Elem("subagent-worker"),
            },
        };
        CanonicalRecord orphanSpan = rootSpan with
        {
            SpanId = "orphan-1",
            ParentSpanId = "missing-parent",
            Name = "orphan.tool",
            Timestamp = "2026-11-14T23:00:02.000Z",
        };
        CanonicalRecord auxSpan = rootSpan with
        {
            SpanId = "aux-1",
            ParentSpanId = null,
            Name = "title",
            Timestamp = "2026-11-14T23:00:03.000Z",
            Cost = new CostBlock(CostBasis.Published, CostStatus.Priced, 0.02, "USD"),
        };

        TimelineNode root = CanonContextAnalysis.BuildTimeline([rootSpan, subagentSpan, orphanSpan, auxSpan]);
        Assert.Equal(CanonContextAnalysis.SessionRootId, root.SpanId);
        Assert.Contains(root.Children, static c => c.SpanId == "root-1");
        Assert.Contains(root.Children, static c => c.SpanId == CanonContextAnalysis.AuxiliaryGroupId);
        Assert.Contains(root.Children, static c => c.SpanId.StartsWith("(orphan:", StringComparison.Ordinal));

        IReadOnlyList<SubagentSession> subagents = CanonContextAnalysis.SubagentSessions(root);
        SubagentSession singleSub = Assert.Single(subagents);
        Assert.Equal("sub-1", singleSub.SpanId);
        Assert.Equal("root-1", singleSub.ParentSpanId);

        CostBlock auxSpend = CanonContextAnalysis.AuxiliarySpend(root);
        Assert.Equal(CostStatus.Priced, auxSpend.Status);
        Assert.Equal(0.02, auxSpend.Value ?? 0, precision: 10);
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

        // Outcome derivation across unmeasured, clean termination, and failure records + population rate
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
                ["type"] = Elem("task_complete"),
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
                ["type"] = Elem("task_failed"),
            }) with
        { Status = "error" };
        OutcomeBlock failureOutcome = CanonFindings.DeriveOutcome([errorTurn]);
        Assert.Equal("failure", failureOutcome.Status);

        OutcomePopulationReport population = CanonFindings.CalculateOutcomePopulationRate([unmeasuredOutcome, successOutcome, failureOutcome]);
        Assert.Equal(3, population.TotalRuns);
        Assert.Equal(2, population.MeasurableCount);
        Assert.Equal(1, population.UnmeasurableCount);
        Assert.Equal(2.0 / 3.0, population.PopulationRate, precision: 10);

        // Exercise detectors end-to-end via DetectFindings + direct detectors + D5/D6/D8 checks.
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
                    ["gen_ai.request.max_context_tokens"] = Elem(100_000),
                    ["kyber.skills.listed"] = Elem(new { isInitial = true, skills = ListedSkillEntries }),
                    ["kyber.skills.invoked"] = Elem(InvokedSkills),
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
                    ["arguments"] = Elem(new { path = "/tmp/same.txt" }),
                    ["gen_ai.tool.result_bytes"] = Elem(12_000),
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
                    ["arguments"] = Elem(new { path = "/tmp/same.txt" }),
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
                    ["gen_ai.request.max_context_tokens"] = Elem(100_000),
                    ["kyber.skills.listed"] = Elem(new { isInitial = true, skills = ListedSkillEntries }),
                    ["kyber.skills.invoked"] = Elem(InvokedSkills),
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
                    ["is_subagent"] = Elem(true),
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
        HashSet<string> firedDetectors = new(findings.Select(static f => f.DetectorId), StringComparer.Ordinal);
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
            if (!string.Equals(finding.MeasurementClass, "coverage-gap", StringComparison.Ordinal))
                Assert.NotNull(finding.ErrorBar);
            Assert.False(string.IsNullOrWhiteSpace(finding.OutcomeRiskCaveat));
            D8LintResult lint = CanonFindings.LintRecommendationD8(finding.Recommendation);
            Assert.True(lint.Valid, $"D8 lint failed for {finding.DetectorId}: {string.Join("; ", lint.Errors)}");
        }

        // Direct measured skill call verifies ErrorBar is populated when SkillTokens are provided.
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
        Assert.NotNull(measuredSkill.ErrorBar);
        Assert.Equal("inferred", measuredSkill.MeasurementClass);

        // Verify D6/D16 tie-breaker places inactive-skill-reference after other detectors with identical RankScore.
        Finding skillTied = findings.First(static f => f.DetectorId == "inactive-skill-reference") with
        {
            RankScore = 500.0,
            Confidence = "measured",
            EstimatedWasteTokens = 5_000,
        };
        Finding duplicateTied = findings.First(static f => f.DetectorId == "duplicate-tool-call") with
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
    public void SessionProjector_IncrementalPassRebuildsOnlyDirtySessionKeys()
    {
        // Twin dedupe collapses identical timestamped LLM turns within the same session inside 60s skew.
        CanonicalRecord primaryTurn = MakeLlmRecord(
            "span-a-1",
            "sess-a",
            "claude-code",
            "2026-11-14T23:00:00.000Z",
            freshInput: 400,
            output: 60,
            cacheRead: 100,
            parts:
            [
                new ContentPart("system_prompt", "You are helpful."),
                new ContentPart("user_prompt", "Fix the bug"),
            ],
            raw: new Dictionary<string, JsonElement>(StringComparer.Ordinal)
            {
                ["gen_ai.response.model"] = Elem("claude-sonnet-4-6"),
                ["gen_ai.request.max_context_tokens"] = Elem(200_000),
            });
        CanonicalRecord fileTwinTurn = primaryTurn with
        {
            SpanId = "span-a-file-twin",
            Timestamp = "2026-11-14T23:00:15.000Z",
            Source = "codeburn/claude-code",
        };
        IReadOnlyList<CanonicalRecord> deduped = CanonSessionProjector.DedupeTwinTurns([primaryTurn, fileTwinTurn], "sess-a");
        CanonicalRecord kept = Assert.Single(deduped);
        Assert.Equal("span-a-1", kept.SpanId);

        // Two genuine non-file OTEL turns with identical counters stay distinct.
        CanonicalRecord otelRetryTurn = primaryTurn with
        {
            SpanId = "span-a-retry",
            Timestamp = "2026-11-14T23:00:15.000Z",
        };
        Assert.Equal(2, CanonSessionProjector.DedupeTwinTurns([primaryTurn, otelRetryTurn], "sess-a").Count);

        using TempDirectory directory = new();
        CanonStore store = new(Path.Combine(directory.Path, "canon.db"));

        CanonicalRecord sessionATurn = primaryTurn;
        CanonicalRecord sessionBTurn1 = MakeLlmRecord(
            "span-b-1",
            "sess-b",
            "codex",
            "2026-11-14T23:01:00.000Z",
            freshInput: 1_500,
            output: 120,
            parts:
            [
                new ContentPart("user_prompt", "Build feature B"),
            ],
            raw: new Dictionary<string, JsonElement>(StringComparer.Ordinal)
            {
                ["gen_ai.response.model"] = Elem("gpt-5.3-codex"),
                ["gen_ai.request.max_context_tokens"] = Elem(200_000),
            });

        store.UpsertRecords([sessionATurn, sessionBTurn1]);

        ProjectionPassReport firstPass = CanonSessionProjector.ProjectIncremental(store);
        Assert.Equal(2, firstPass.Built);
        Assert.Equal(0, firstPass.Skipped);
        Assert.Equal(0, firstPass.Pruned);
        Assert.Contains("sess-a", firstPass.RebuiltKeys);
        Assert.Contains("sess-b", firstPass.RebuiltKeys);

        SessionSummaryRow? sessA = store.GetSession("sess-a");
        Assert.NotNull(sessA);
        Assert.Equal("claude-code", sessA.Harness);
        Assert.True(sessA.Payload.TryGetProperty("turns", out JsonElement turnsProp));
        Assert.Equal(JsonValueKind.Array, turnsProp.ValueKind);
        Assert.Equal(1, turnsProp.GetArrayLength());

        // Write a new turn only for sess-b and add oversized result in sess-b so findings persist too.
        CanonicalRecord sessionBTool = new()
        {
            SpanId = "span-b-tool",
            TraceId = "trace-sess-b",
            ParentSpanId = "span-b-1",
            Source = "codex",
            Harness = "codex",
            SessionId = "sess-b",
            Name = "search_docs",
            Op = "tool.invoke",
            Kind = "internal",
            Timestamp = "2026-11-14T23:01:05.000Z",
            DurationMs = 45,
            Status = "ok",
            Tokens = new TokenUsage(0, 0, 0, 0, 0, 0),
            Content = new Dictionary<string, string>(StringComparer.Ordinal),
            Parts =
            [
                new ContentPart("tool_result_content", new string('z', 15_000), Tokens: 3_750),
            ],
            Cost = new CostBlock(CostBasis.Published, CostStatus.OutOfScope),
            Raw = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
            {
                ["gen_ai.tool.result_bytes"] = Elem(15_000),
            },
        };

        store.UpsertRecords([sessionBTool]);

        ProjectionPassReport secondPass = CanonSessionProjector.ProjectIncremental(store);
        Assert.Equal(1, secondPass.Built);
        Assert.Equal(1, secondPass.Skipped);
        Assert.Contains("sess-b", secondPass.RebuiltKeys);
        Assert.Contains("sess-a", secondPass.SkippedKeys);
        Assert.DoesNotContain("sess-a", secondPass.RebuiltKeys);
        Assert.True(secondPass.Findings >= 1);

        IReadOnlyList<Finding> sessBFindings = store.ListFindings(sessionId: "sess-b");
        Assert.Contains(sessBFindings, static f => f.DetectorId == "oversized-tool-result");

        // Third pass with no writes skips both built sessions cleanly.
        ProjectionPassReport thirdPass = CanonSessionProjector.ProjectIncremental(store);
        Assert.Equal(0, thirdPass.Built);
        Assert.Equal(2, thirdPass.Skipped);
        Assert.Empty(thirdPass.RebuiltKeys);
    }

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

    private static JsonElement Elem(object value) => JsonSerializer.SerializeToElement(value);
}
