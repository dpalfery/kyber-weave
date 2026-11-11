using KyberWeave.Core.Arbiter;
using KyberWeave.Core.Arbiter.Rules;
using Xunit;

namespace KyberWeave.Tests.Arbiter;

/// <summary>
/// Contract for the decision-model savings families: the four new trigger
/// families (ModelTarget, Shortcut, FailureCluster, Advice), the new effects on
/// LensReturned and RefuteSpawn, the unchanged families, the strongest-effect
/// short-circuit set, and the defaults of <c>ArbiterRule.Shadow</c> and
/// <c>RuleAsk.ForEach</c>. RED: the families, the new effects, <c>Shadow</c> and
/// <c>ForEach</c> do not exist yet, so this file does not compile.
/// </summary>
public sealed class ArbiterFamilyEffectsTests
{
    private const string Keep = "keep";
    private const string Retarget = "retarget";
    private const string Spawn = "spawn";
    private const string Answer = "answer";
    private const string SameFailure = "same-failure";
    private const string Advise = "advise";
    private const string Drop = "drop";
    private const string Duplicate = "duplicate";

    private static ArbiterRule MinimalRule() => new(
        "KW-ARB-TEST-001",
        "lens.returned",
        "Is the finding already covered?",
        ["allow", "annotate"],
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["allow"] = RuleEffects.Allow,
            ["annotate"] = RuleEffects.Annotate,
        });

    [Fact]
    public void ModelTarget_AllowedEffects_AreKeepAndRetarget()
    {
        Assert.Equal(
            new[] { Keep, Retarget }.Order(),
            ArbiterTriggerFamilies.AllowedEffects(ArbiterTriggerFamily.ModelTarget).Order());
    }

    [Fact]
    public void ModelTarget_Undecidable_MapsToKeep()
    {
        Assert.Equal(Keep, ArbiterTriggerFamilies.UndecidableEffect(ArbiterTriggerFamily.ModelTarget));
    }

    [Fact]
    public void ModelTarget_Combine_RetargetWinsWhenAnyRuleAnswersIt()
    {
        Assert.Equal(Retarget, ArbiterTriggerFamilies.Combine(ArbiterTriggerFamily.ModelTarget, [Keep, Retarget]));
        Assert.Equal(Keep, ArbiterTriggerFamilies.Combine(ArbiterTriggerFamily.ModelTarget, [Keep, Keep]));
    }

    [Fact]
    public void Shortcut_AllowedEffects_AreSpawnAnswerAndKeep()
    {
        // Keep is legitimate: the triage and intake rules keep the dispatch when the Arbiter is unsure.
        Assert.Equal(
            new[] { Spawn, Answer, Keep }.Order(),
            ArbiterTriggerFamilies.AllowedEffects(ArbiterTriggerFamily.Shortcut).Order());
    }

    [Fact]
    public void Shortcut_Undecidable_MapsToSpawn()
    {
        Assert.Equal(Spawn, ArbiterTriggerFamilies.UndecidableEffect(ArbiterTriggerFamily.Shortcut));
    }

    [Fact]
    public void Shortcut_Combine_AnswerOnlyWhenEveryRuleAnswers()
    {
        Assert.Equal(Answer, ArbiterTriggerFamilies.Combine(ArbiterTriggerFamily.Shortcut, [Answer]));
        Assert.Equal(Answer, ArbiterTriggerFamilies.Combine(ArbiterTriggerFamily.Shortcut, [Answer, Answer]));
    }

    [Fact]
    public void Shortcut_Combine_AnySpawnGivesSpawn()
    {
        Assert.Equal(Spawn, ArbiterTriggerFamilies.Combine(ArbiterTriggerFamily.Shortcut, [Answer, Spawn]));
    }

    [Fact]
    public void Shortcut_Combine_UndecidableGivesSpawn()
    {
        string undecidable = ArbiterTriggerFamilies.UndecidableEffect(ArbiterTriggerFamily.Shortcut);

        Assert.Equal(Spawn, ArbiterTriggerFamilies.Combine(ArbiterTriggerFamily.Shortcut, [Answer, undecidable]));
    }

    [Fact]
    public void FailureCluster_AllowedEffects_AreAllowAndSameFailure()
    {
        Assert.Equal(
            new[] { RuleEffects.Allow, SameFailure }.Order(),
            ArbiterTriggerFamilies.AllowedEffects(ArbiterTriggerFamily.FailureCluster).Order());
    }

    [Fact]
    public void FailureCluster_Undecidable_MapsToAllow()
    {
        Assert.Equal(RuleEffects.Allow, ArbiterTriggerFamilies.UndecidableEffect(ArbiterTriggerFamily.FailureCluster));
    }

    [Fact]
    public void FailureCluster_Combine_SameFailureWinsWhenAnyRuleAnswersIt()
    {
        Assert.Equal(
            SameFailure,
            ArbiterTriggerFamilies.Combine(ArbiterTriggerFamily.FailureCluster, [RuleEffects.Allow, SameFailure]));
        Assert.Equal(
            RuleEffects.Allow,
            ArbiterTriggerFamilies.Combine(ArbiterTriggerFamily.FailureCluster, [RuleEffects.Allow]));
    }

    [Fact]
    public void Advice_AllowedEffects_AreKeepAndAdvise()
    {
        Assert.Equal(
            new[] { Keep, Advise }.Order(),
            ArbiterTriggerFamilies.AllowedEffects(ArbiterTriggerFamily.Advice).Order());
    }

    [Fact]
    public void Advice_Undecidable_MapsToKeep()
    {
        Assert.Equal(Keep, ArbiterTriggerFamilies.UndecidableEffect(ArbiterTriggerFamily.Advice));
    }

    [Fact]
    public void Advice_Combine_AdviseOnlyWhenEveryRuleAnswersIt()
    {
        Assert.Equal(Advise, ArbiterTriggerFamilies.Combine(ArbiterTriggerFamily.Advice, [Advise, Advise]));
        Assert.Equal(Keep, ArbiterTriggerFamilies.Combine(ArbiterTriggerFamily.Advice, [Advise, Keep]));
    }

    [Fact]
    public void LensReturned_AllowedEffects_IncludeDrop()
    {
        Assert.Equal(
            new[] { RuleEffects.Allow, RuleEffects.Annotate, Drop }.Order(),
            ArbiterTriggerFamilies.AllowedEffects(ArbiterTriggerFamily.LensReturned).Order());
    }

    [Fact]
    public void LensReturned_Combine_AnnotateSuppressesDrop()
    {
        Assert.Equal(
            RuleEffects.Annotate,
            ArbiterTriggerFamilies.Combine(ArbiterTriggerFamily.LensReturned, [RuleEffects.Allow, Drop, RuleEffects.Annotate]));
    }

    [Fact]
    public void LensReturned_Combine_DropWinsOverAllowWhenNoAnnotate()
    {
        Assert.Equal(
            Drop,
            ArbiterTriggerFamilies.Combine(ArbiterTriggerFamily.LensReturned, [RuleEffects.Allow, Drop]));
        Assert.Equal(
            RuleEffects.Allow,
            ArbiterTriggerFamilies.Combine(ArbiterTriggerFamily.LensReturned, [RuleEffects.Allow]));
    }

    [Fact]
    public void RefuteSpawn_AllowedEffects_IncludeDropAndDuplicate()
    {
        Assert.Equal(
            new[] { RuleEffects.Allow, RuleEffects.Verify, Drop, Duplicate }.Order(),
            ArbiterTriggerFamilies.AllowedEffects(ArbiterTriggerFamily.RefuteSpawn).Order());
    }

    [Fact]
    public void RefuteSpawn_Combine_VerifyBeatsDropBeatsDuplicateBeatsAllow()
    {
        Assert.Equal(
            RuleEffects.Verify,
            ArbiterTriggerFamilies.Combine(ArbiterTriggerFamily.RefuteSpawn, [Duplicate, Drop, RuleEffects.Verify, RuleEffects.Allow]));
        Assert.Equal(
            Drop,
            ArbiterTriggerFamilies.Combine(ArbiterTriggerFamily.RefuteSpawn, [RuleEffects.Allow, Duplicate, Drop]));
        Assert.Equal(
            Duplicate,
            ArbiterTriggerFamilies.Combine(ArbiterTriggerFamily.RefuteSpawn, [RuleEffects.Allow, Duplicate]));
        Assert.Equal(
            RuleEffects.Allow,
            ArbiterTriggerFamilies.Combine(ArbiterTriggerFamily.RefuteSpawn, [RuleEffects.Allow]));
    }

    [Theory]
    [InlineData(RuleEffects.Escalate, true)]
    [InlineData(RuleEffects.Skip, true)]
    [InlineData(RuleEffects.Verify, true)]
    [InlineData(Drop, false)]
    [InlineData(Duplicate, false)]
    [InlineData(Retarget, false)]
    [InlineData(Keep, false)]
    [InlineData(Spawn, false)]
    [InlineData(Answer, false)]
    [InlineData(Advise, false)]
    [InlineData(SameFailure, false)]
    [InlineData(RuleEffects.Annotate, false)]
    [InlineData(RuleEffects.Allow, false)]
    public void IsStrongestEffect_OnlyEscalateSkipVerify_ShortCircuit(string effect, bool expected)
    {
        Assert.Equal(expected, ArbiterTriggerFamilies.IsStrongestEffect(effect));
    }

    [Fact]
    public void UnchangedFamilies_KeepTheirAllowedEffects()
    {
        Assert.Equal(
            new[] { RuleEffects.Allow, RuleEffects.Escalate }.Order(),
            ArbiterTriggerFamilies.AllowedEffects(ArbiterTriggerFamily.Conductor).Order());
        Assert.Equal(
            new[] { RuleEffects.Allow, RuleEffects.Escalate }.Order(),
            ArbiterTriggerFamilies.AllowedEffects(ArbiterTriggerFamily.Investigate).Order());
        Assert.Equal(
            new[] { RuleEffects.Allow, RuleEffects.Skip }.Order(),
            ArbiterTriggerFamilies.AllowedEffects(ArbiterTriggerFamily.LensSpawn).Order());
        Assert.Equal(
            new[] { RuleEffects.Applies, RuleEffects.NotApplicable }.Order(),
            ArbiterTriggerFamilies.AllowedEffects(ArbiterTriggerFamily.Gate).Order());
    }

    [Fact]
    public void UnchangedFamilies_KeepTheirUndecidableEffects()
    {
        Assert.Equal(RuleEffects.Escalate, ArbiterTriggerFamilies.UndecidableEffect(ArbiterTriggerFamily.Conductor));
        Assert.Equal(RuleEffects.Escalate, ArbiterTriggerFamilies.UndecidableEffect(ArbiterTriggerFamily.Investigate));
        Assert.Equal(RuleEffects.Allow, ArbiterTriggerFamilies.UndecidableEffect(ArbiterTriggerFamily.LensSpawn));
        Assert.Equal(RuleEffects.Applies, ArbiterTriggerFamilies.UndecidableEffect(ArbiterTriggerFamily.Gate));
    }

    [Fact]
    public void ArbiterRule_Shadow_DefaultsToFalse()
    {
        ArbiterRule rule = MinimalRule();

        Assert.False(rule.Shadow);
    }

    [Fact]
    public void RuleAsk_ForEach_DefaultsToNull()
    {
        RuleAsk ask = new(
            "choice",
            new Dictionary<string, string>(StringComparer.Ordinal));

        Assert.Null(ask.ForEach);
    }
}
