using KyberWeave.Core.Arbiter.Facts;
using KyberWeave.Core.Arbiter.Rules;

namespace KyberWeave.Core.Arbiter.ModelTargets;

/// <summary>
/// Resolves the model target a <c>delegate.model</c> dispatch is pointed at, and whether the harness can
/// deliver it. The evaluator runs it after the secondary's outcome is combined and writes the result to the
/// decision's <c>model</c> block and to the secondary payload.
/// </summary>
/// <remarks>
/// The profile comes from the ladder when the ladder rule wants a retarget, which is a retry climbing one
/// rung, and otherwise from the difficulty answer. A rule wants a retarget when it is enforced to retarget,
/// or when it is a shadow rule whose would-be effect is a retarget: shadow records the choice the enforced
/// mode would make, so the block still names it, with the reason <c>shadow</c>. The steps then run in this
/// order, and the first that holds sets the reason and stops delivery: the caller named a model, the table
/// is unreadable, the profile has no token, the token is already the rendered one, or the harness has no
/// model override. Shadow mode stops last. Only a dispatch that clears every step is delivered.
/// </remarks>
internal static class ModelTargetResolver
{
    /// <summary>The caller named a model on the dispatch, so the Arbiter does not change it.</summary>
    public const string CallerNamedModel = "caller-named-model";

    /// <summary>The model targets table for the harness is absent, unreadable or not a valid v1 table.</summary>
    public const string ModelTargetsUnreadable = "model-targets-unreadable";

    /// <summary>The targeted profile has no model token in the table.</summary>
    public const string NoToken = "no-token";

    /// <summary>The targeted token is the one the target agent is already rendered with.</summary>
    public const string SameAsRendered = "same-as-rendered";

    /// <summary>The harness offers no model override field, so the token cannot be delivered.</summary>
    public const string UnsupportedHarness = "unsupported-harness";

    /// <summary>The targeted token is not an alias the harness accepts, so no adapter would write it.</summary>
    public const string TokenNotAccepted = "token-not-accepted";

    /// <summary>The adapter could not write the accepted model into the dispatch, so the dispatch runs without it.</summary>
    public const string ModelWriteFailed = "model-write-failed";

    /// <summary>The mode is shadow: the choice is recorded and not delivered.</summary>
    public const string Shadow = "shadow";

    /// <summary>The mode is not exactly <c>enforce</c> or <c>shadow</c>: nothing is delivered and the mode is reported.</summary>
    public const string UnknownMode = "unknown-mode";

    private const string ModeEnforce = "enforce";
    private const string ModeShadow = "shadow";
    private const string LadderNextFact = "model-targeting.ladder.next";
    private const string SourceLadder = "ladder";
    private const string SourceDifficulty = "difficulty";
    private const string PriorFail = "fail";

    /// <summary>
    /// Returns the model block for the dispatch, or null when no rule wants a retarget or no profile follows
    /// from the answers.
    /// </summary>
    /// <param name="rules">The decision's model-targeting rules, with their effect and would-be effect.</param>
    /// <param name="facts">The dispatch's facts, which carry the ladder's next rung.</param>
    /// <param name="targeting">The model-targeting block, which maps difficulties to profiles.</param>
    /// <param name="table">The harness's model targets table, or null when it could not be read.</param>
    /// <param name="ev">The event, which carries the caller's model, the rendered token and the override field.</param>
    /// <param name="mode">The model-targeting mode.</param>
    public static ArbiterModelBlock? Resolve(
        IReadOnlyList<ArbiterDecisionRule> rules,
        ArbiterFactSet facts,
        ArbiterModelTargetingConfig targeting,
        ArbiterModelTargets? table,
        ArbiterEvent ev,
        string mode)
    {
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentNullException.ThrowIfNull(targeting);
        ArgumentNullException.ThrowIfNull(ev);
        ArgumentNullException.ThrowIfNull(mode);

        (string? profile, string? source) = Choose(rules, facts, targeting);
        if (profile is null || source is null)
        {
            return null;
        }

        string? token = table is not null && table.Profiles.TryGetValue(profile, out string? found) ? found : null;
        string? reason = ReasonFor(ev, table, token, mode);
        return new ArbiterModelBlock(profile, token, ev.RenderedModelToken, source, reason is null, reason);
    }

    private static (string? Profile, string? Source) Choose(
        IReadOnlyList<ArbiterDecisionRule> rules,
        ArbiterFactSet facts,
        ArbiterModelTargetingConfig targeting)
    {
        if (rules.Any(rule => rule.Id == ModelTargetingRules.LadderRuleId && WantsRetarget(rule))
            && facts.TryGet(LadderNextFact, out ArbiterFact? next)
            && next.Value is string ladderProfile)
        {
            return (ladderProfile, SourceLadder);
        }

        // A failed retry is never sent cheaper by difficulty: the difficulty answer applies to a first attempt or a prior pass only.
        if (facts.TryGet(ModelTargetFacts.PriorResult, out ArbiterFact? prior)
            && string.Equals(prior.Value as string, PriorFail, StringComparison.Ordinal))
        {
            return (null, null);
        }

        ArbiterDecisionRule? difficulty = rules.FirstOrDefault(rule => rule.Id == ModelTargetingRules.DifficultyRuleId && WantsRetarget(rule));
        if (difficulty is not null && targeting.DifficultyProfiles.TryGetValue(difficulty.Answer, out string? difficultyProfile))
        {
            return (difficultyProfile, SourceDifficulty);
        }

        return (null, null);
    }

    /// <summary>A rule wants a retarget when enforced to, or when shadowed and its would-be effect is one.</summary>
    private static bool WantsRetarget(ArbiterDecisionRule rule) =>
        string.Equals(rule.Effect, RuleEffects.Retarget, StringComparison.Ordinal)
        || (rule.Shadow == true && string.Equals(rule.WouldEffect, RuleEffects.Retarget, StringComparison.Ordinal));

    /// <summary>The first step that holds, in the contract's order; null when the dispatch is delivered.</summary>
    private static string? ReasonFor(ArbiterEvent ev, ArbiterModelTargets? table, string? token, string mode)
    {
        if (!string.IsNullOrWhiteSpace(ev.Model))
        {
            return CallerNamedModel;
        }

        if (table is null)
        {
            return ModelTargetsUnreadable;
        }

        if (string.IsNullOrWhiteSpace(token))
        {
            return NoToken;
        }

        if (string.Equals(token, ev.RenderedModelToken, StringComparison.Ordinal))
        {
            return SameAsRendered;
        }

        if (ev.ModelOverrideField is null)
        {
            return UnsupportedHarness;
        }

        if (ev.ModelOverrideAliases is { } accepted && ModelTokenAliases.AliasFor(token, accepted) is null)
        {
            return TokenNotAccepted;
        }

        // Only the exact enforce mode delivers. Any other mode is shadow-like: nothing is written, and the reason says why.
        return mode switch
        {
            ModeEnforce => null,
            ModeShadow => Shadow,
            _ => UnknownMode,
        };
    }
}
