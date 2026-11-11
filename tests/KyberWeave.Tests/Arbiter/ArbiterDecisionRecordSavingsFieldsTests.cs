using System.Text.Json;
using KyberWeave.Core.Arbiter.Facts;
using KyberWeave.Core.Arbiter.Rules;
using Xunit;

namespace KyberWeave.Tests.Arbiter;

/// <summary>
/// Contract for task 1.5: the savings fields on the decision and ledger records.
/// RED: <c>ArbiterDecisionRecord.PrimaryLedgerId/Model/Substitute/DuplicateOf</c>,
/// <c>ArbiterDecisionRule.Shadow/WouldEffect/Candidate</c> and
/// <c>ArbiterLedgerEvent.ModelProfile/ModelToken/ModelSource</c> do not exist yet.
/// </summary>
public sealed class ArbiterDecisionRecordSavingsFieldsTests
{
    private static readonly DateTimeOffset At = new(2025, 10, 1, 12, 0, 0, TimeSpan.Zero);

    private static ArbiterDecisionRecord Record(
        string? primaryLedgerId = null,
        ArbiterModelBlock? model = null,
        bool? substitute = null,
        string? duplicateOf = null,
        IReadOnlyList<ArbiterDecisionRule>? rules = null) =>
        new(
            "rec-1",
            At,
            "hook",
            "claude",
            null,
            "ledger-1",
            "delegate",
            "conductor",
            "hook",
            "csharp-dev",
            null,
            null,
            null,
            "rule-set",
            rules ?? [],
            new ArbiterProviderRecord("none", null, null, null, null, ArbiterProviderStatuses.NotEvaluated),
            RuleEffects.Allow,
            1,
            0,
            primaryLedgerId: primaryLedgerId,
            model: model,
            substitute: substitute,
            duplicateOf: duplicateOf);

    private static ArbiterDecisionRule Rule(
        bool? shadow = null,
        string? wouldEffect = null,
        string? candidate = null) =>
        new(
            "KW-ARB-ASK-001",
            1,
            "within-task",
            null,
            null,
            null,
            RuleEffects.Allow,
            [],
            null,
            shadow: shadow,
            wouldEffect: wouldEffect,
            candidate: candidate);

    private static JsonElement TopLevel(string json) => JsonDocument.Parse(json).RootElement;

    [Fact]
    public void NewDecisionFields_OmittedWhenNull()
    {
        JsonElement root = TopLevel(ArbiterJson.Serialize(Record()));

        Assert.False(root.TryGetProperty("primary-ledger-id", out _));
        Assert.False(root.TryGetProperty("model", out _));
        Assert.False(root.TryGetProperty("substitute", out _));
        Assert.False(root.TryGetProperty("duplicate-of", out _));
    }

    [Fact]
    public void NewDecisionFields_WrittenWhenSet()
    {
        JsonElement root = TopLevel(ArbiterJson.Serialize(Record(
            primaryLedgerId: "ledger-0",
            model: new ArbiterModelBlock("fast", "claude-haiku-5-5", "claude-sonnet-5-5", "ladder", true, null),
            substitute: true,
            duplicateOf: "rec-0")));

        Assert.Equal("ledger-0", root.GetProperty("primary-ledger-id").GetString());
        JsonElement model = root.GetProperty("model");
        Assert.Equal(JsonValueKind.Object, model.ValueKind);
        Assert.Equal("fast", model.GetProperty("profile").GetString());
        Assert.Equal("claude-haiku-5-5", model.GetProperty("token").GetString());
        Assert.Equal("claude-sonnet-5-5", model.GetProperty("rendered-token").GetString());
        Assert.Equal("ladder", model.GetProperty("source").GetString());
        Assert.True(model.GetProperty("delivered").GetBoolean());
        Assert.False(model.TryGetProperty("reason", out _));
        Assert.True(root.GetProperty("substitute").GetBoolean());
        Assert.Equal("rec-0", root.GetProperty("duplicate-of").GetString());
    }

    [Fact]
    public void NewRuleFields_OmittedWhenNullAndWrittenWhenSet()
    {
        JsonElement bare = TopLevel(ArbiterJson.Serialize(Record(rules: [Rule()])))
            .GetProperty("rules")[0];
        Assert.False(bare.TryGetProperty("shadow", out _));
        Assert.False(bare.TryGetProperty("would-effect", out _));
        Assert.False(bare.TryGetProperty("candidate", out _));

        JsonElement set = TopLevel(ArbiterJson.Serialize(Record(rules: [Rule(
            shadow: true,
            wouldEffect: RuleEffects.Retarget,
            candidate: "src/a.cs")])))
            .GetProperty("rules")[0];
        Assert.True(set.GetProperty("shadow").GetBoolean());
        Assert.Equal(RuleEffects.Retarget, set.GetProperty("would-effect").GetString());
        Assert.Equal("src/a.cs", set.GetProperty("candidate").GetString());
    }

    [Fact]
    public void SchemaId_StaysDecisionV1()
    {
        Assert.Equal("kyber-arbiter.decision/v1", ArbiterDecisionRecord.SchemaName);
        Assert.Equal("kyber-arbiter.decision/v1", TopLevel(ArbiterJson.Serialize(Record())).GetProperty("schema").GetString());
    }

    [Fact]
    public void OlderDecisionLine_WithoutNewFields_StillDeserialises()
    {
        // A line written before the savings fields existed: every new field is null, so it is omitted.
        string olderLine = ArbiterJson.Serialize(Record());

        ArbiterDecisionRecord? read = JsonSerializer.Deserialize<ArbiterDecisionRecord>(olderLine, ArbiterJson.Options);

        Assert.NotNull(read);
        Assert.Equal("ledger-1", read!.LedgerId);
        Assert.Null(read.PrimaryLedgerId);
        Assert.Null(read.Model);
        Assert.Null(read.Substitute);
        Assert.Null(read.DuplicateOf);
    }

    [Fact]
    public void LedgerEvent_ModelFieldsOmittedWhenNullAndWrittenWhenSet()
    {
        ArbiterLedgerEvent bare = new("ev-1", At, "hook", "claude", null, ArbiterLedgerPhases.Pre);
        JsonElement bareRoot = TopLevel(ArbiterJson.Serialize(bare));
        Assert.False(bareRoot.TryGetProperty("model-profile", out _));
        Assert.False(bareRoot.TryGetProperty("model-token", out _));
        Assert.False(bareRoot.TryGetProperty("model-source", out _));

        ArbiterLedgerEvent set = new("ev-2", At, "hook", "claude", null, ArbiterLedgerPhases.Pre)
        {
            ModelProfile = "balanced",
            ModelToken = "claude-haiku-5-5",
            ModelSource = "model-targets",
        };
        JsonElement root = TopLevel(ArbiterJson.Serialize(set));
        Assert.Equal("balanced", root.GetProperty("model-profile").GetString());
        Assert.Equal("claude-haiku-5-5", root.GetProperty("model-token").GetString());
        Assert.Equal("model-targets", root.GetProperty("model-source").GetString());
    }
}
