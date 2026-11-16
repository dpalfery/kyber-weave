using KyberWeave.Core.Squad.Rendering;

namespace KyberWeave.Core.Squad.Deployment;

/// <summary>The blocks and degradation records left after Factory shadowing was resolved.</summary>
/// <param name="Blocks">
/// The render's blocks with any shadowed Factory block removed; null stays null so a
/// render that produced no blocks keeps producing a receipt without a blocks field.
/// </param>
/// <param name="Degradations">One record per dropped block, in drop order.</param>
public sealed record FactoryHooksShadowingOutcome(
    IReadOnlyList<SquadRenderedBlock>? Blocks,
    IReadOnlyList<SquadDegradationRecord> Degradations);

/// <summary>What the target's Factory files say about the settings hooks.</summary>
public enum FactoryHooksShadowState
{
    /// <summary>Squad's block can be spliced without hiding any user hook.</summary>
    Clear,

    /// <summary><c>settings.json</c> holds a <c>hooks</c> key that a new <c>hooks.json</c> would hide.</summary>
    Shadowed,

    /// <summary><c>settings.json</c> does not parse as strict JSON, so whether it holds hooks is unknown.</summary>
    Unparsable,
}
