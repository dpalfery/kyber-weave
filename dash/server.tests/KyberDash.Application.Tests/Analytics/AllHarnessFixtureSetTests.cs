using KyberDash.Application.Tests.Fakes;
using KyberDash.Contracts.Repositories;
using KyberDash.Data.Collector;
using KyberDash.Data.Collector.Sources;
using KyberDash.Domain;
using Xunit;

namespace KyberDash.Application.Tests.Analytics;

/// <summary>
/// Verifies <see cref="AllHarnessFixtureSet"/> loads synthetic fixtures from every evidenced
/// mapper into <see cref="InMemoryRelationalStore"/> covering all required ops, kinds,
/// harness folds, the <c>antigravity-cli</c> statusline, <c>zcode</c> unevidenced emission,
/// and legacy-breaking numeric/boolean content shapes.
/// </summary>
public sealed class AllHarnessFixtureSetTests
{
    /// <summary>Every evidenced recoverable BL-2 harness ID has at least one session and record.</summary>
    [Fact]
    public void CreateStore_CoversEveryEvidencedRecoverableHarnessId()
    {
        InMemoryRelationalStore store = AllHarnessFixtureSet.CreateStore();

        foreach (string harnessId in AllHarnessFixtureSet.EvidencedRecoverableHarnessIds)
        {
            Assert.NotEmpty(store.Sessions(harnessId));
            Assert.True(
                store.CountRecords(harnessId) > 0,
                $"Harness '{harnessId}' has no records in the fixture set.");
        }
    }

    /// <summary>The recoverable subset excludes OTLP-only, unevidenced, classified-only, and BL-3 IDs.</summary>
    [Fact]
    public void EvidencedRecoverableHarnessIds_ExcludesNonRecoverableHarnessIds()
    {
        Assert.DoesNotContain("zcode", AllHarnessFixtureSet.EvidencedRecoverableHarnessIds, StringComparer.Ordinal);
        Assert.DoesNotContain("opencode", AllHarnessFixtureSet.EvidencedRecoverableHarnessIds, StringComparer.Ordinal);
        Assert.DoesNotContain("copilot", AllHarnessFixtureSet.EvidencedRecoverableHarnessIds, StringComparer.Ordinal);
        Assert.DoesNotContain("copilot-agent", AllHarnessFixtureSet.EvidencedRecoverableHarnessIds, StringComparer.Ordinal);
        Assert.DoesNotContain("claude-unclassified", AllHarnessFixtureSet.EvidencedRecoverableHarnessIds, StringComparer.Ordinal);
        Assert.DoesNotContain("codex-desktop", AllHarnessFixtureSet.EvidencedRecoverableHarnessIds, StringComparer.Ordinal);
        Assert.DoesNotContain("windsurf", AllHarnessFixtureSet.EvidencedRecoverableHarnessIds, StringComparer.Ordinal);
        Assert.DoesNotContain("aider", AllHarnessFixtureSet.EvidencedRecoverableHarnessIds, StringComparer.Ordinal);
    }

    /// <summary>The fixture set produces <c>llm.invoke</c>, <c>tool.invoke</c>, and <c>agent.invoke</c>.</summary>
    [Fact]
    public void CreateStore_CoversAllRequiredOps()
    {
        InMemoryRelationalStore store = AllHarnessFixtureSet.CreateStore();

        List<RecordOp> ops = [.. store.AllSessionRecordsForMaintenance().Select(static r => r.Op)];

        Assert.Contains(RecordOp.LlmInvoke, ops);
        Assert.Contains(RecordOp.ToolInvoke, ops);
        Assert.Contains(RecordOp.AgentInvoke, ops);
    }

    /// <summary>The fixture set produces both <c>internal</c> and <c>client</c> span kinds.</summary>
    [Fact]
    public void CreateStore_CoversInternalAndClientKinds()
    {
        InMemoryRelationalStore store = AllHarnessFixtureSet.CreateStore();

        List<RecordKind> kinds = [.. store.AllSessionRecordsForMaintenance().Select(static r => r.Kind)];

        Assert.Contains(RecordKind.Internal, kinds);
        Assert.Contains(RecordKind.Client, kinds);
    }

    /// <summary>
    /// Alias harnesses fold onto their canonical IDs and the folded fixtures are stored canonically.
    /// </summary>
    [Fact]
    public void CreateStore_FoldsDesktopAndCursorAgentAliases()
    {
        Assert.Equal("claude-code", CanonPricing.NormalizeHarnessName(AllHarnessFixtureSet.ClaudeDesktopAlias));
        Assert.Equal("cursor", CanonPricing.NormalizeHarnessName(AllHarnessFixtureSet.CursorAgentAlias));

        InMemoryRelationalStore store = AllHarnessFixtureSet.CreateStore();

        CollectedSession? desktopSession = store
            .Sessions("claude-code")
            .SingleOrDefault(static s => s.NativeSessionId != null && s.NativeSessionId.Contains("claude-desktop", StringComparison.Ordinal));
        Assert.NotNull(desktopSession);

        CollectedSession? agentSession = store
            .Sessions("cursor")
            .SingleOrDefault(static s => s.NativeSessionId != null && s.NativeSessionId.Contains("cursor-agent", StringComparison.Ordinal));
        Assert.NotNull(agentSession);
    }

    /// <summary>The fixture set carries an <c>antigravity-cli</c> source from the statusline locator.</summary>
    [Fact]
    public void CreateStore_ContainsAntigravityCliStatuslineSource()
    {
        InMemoryRelationalStore store = AllHarnessFixtureSet.CreateStore();

        List<StoredSource> sources = [.. store.Sources("antigravity-cli")];

        Assert.Contains(
            sources,
            static s => s.Source.Locator.EndsWith("cache/antigravity-statusline.jsonl", StringComparison.Ordinal));
        Assert.True(
            store.CountRecords("antigravity-cli") > 0,
            "antigravity-cli has no records in the fixture set.");
    }

    /// <summary><c>zcode</c> resolves as unevidenced and emits a <c>SOURCE_FORMAT_UNEVIDENCED</c> problem.</summary>
    [Fact]
    public void CreateStore_RecordsZcodeUnevidencedProblem()
    {
        HarnessSourceRegistry registry = new();

        Assert.Equal(HarnessSourceSupport.Unevidenced, registry.Resolve("zcode")?.Support);

        InMemoryRelationalStore store = AllHarnessFixtureSet.CreateStore();

        StoredIngestProblem? problem = store
            .ListIngestProblems()
            .SingleOrDefault(static p => string.Equals(p.Problem.HarnessId, "zcode", StringComparison.Ordinal));
        Assert.NotNull(problem);
        Assert.Equal(HarnessSourceRegistry.UnevidencedSourceFormatCode, problem.Problem.Code);
    }

    /// <summary>Numeric and boolean legacy-breaking content survives the relational round-trip as text.</summary>
    [Fact]
    public void CreateStore_PreservesNumericAndBooleanLegacyBreakingShapes()
    {
        InMemoryRelationalStore store = AllHarnessFixtureSet.CreateStore();

        CollectedRecord? numericResult = store.FindRecord(AllHarnessFixtureSet.NumericToolResultSpanId);
        Assert.NotNull(numericResult);
        Assert.Equal("42", numericResult.Tool?.Result?.PlainText);

        CollectedRecord? booleanPart = store.FindRecord(AllHarnessFixtureSet.BooleanContentSpanId);
        Assert.NotNull(booleanPart);
        Assert.Contains(
            store.FindRecordParts(AllHarnessFixtureSet.BooleanContentSpanId),
            static p => p.PlainText == "true");

        CollectedRecord? mixedJsonPart = store.FindRecord(AllHarnessFixtureSet.MixedJsonContentSpanId);
        Assert.NotNull(mixedJsonPart);
        CollectedContentPart storedMixed = Assert.Single(
            store.FindRecordParts(AllHarnessFixtureSet.MixedJsonContentSpanId));
        Assert.Contains("3", storedMixed.PlainText, StringComparison.Ordinal);
        Assert.Contains("false", storedMixed.PlainText, StringComparison.Ordinal);
    }
}
