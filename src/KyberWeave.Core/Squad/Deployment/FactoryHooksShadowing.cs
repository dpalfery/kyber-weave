using System.Text.Json;
using System.Text.Json.Nodes;
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

/// <summary>
/// Keeps Squad's Factory hook block from shadowing the user's own hooks (design §10.8, R18).
/// </summary>
/// <remarks>
/// <para>
/// Factory reads the <c>hooks</c> key of <c>.factory/settings.json</c> only when
/// <c>.factory/hooks.json</c> is absent [FA-hooks]. Splicing Squad's block into a newly
/// created <c>hooks.json</c> would therefore silently disable every hook the user keeps
/// in <c>settings.json</c> — a loss, not a merge, and irreversible once the user deletes
/// the old key. When that shape is detected the lifecycle drops the Factory block and
/// records <c>arbiter-not-enforced</c> with <c>settings-hooks-shadowed</c>, naming the
/// fix: move the user's hooks into <c>.factory/hooks.json</c>, then run
/// <c>squad update</c>. Under-enforcement is visible in the receipt; data loss is not
/// recoverable, which is why the check fails closed.
/// </para>
/// <para>
/// The check lives in the lifecycle rather than the renderer on purpose: the renderer is
/// a pure function of its request and never touches the target root, while shadowing is
/// a property of the deployment target's current files. The lifecycle calls
/// <see cref="Resolve"/> while building the plan, so a dry run reports the drop too.
/// </para>
/// </remarks>
public static class FactoryHooksShadowing
{
    private const string FactoryTargetToken = "factory";
    private const string HooksJsonRelativePath = ".factory/hooks.json";
    private const string SettingsJsonRelativePath = ".factory/settings.json";
    private const string SettingsHooksKey = "hooks";

    /// <summary>The receipt degradation code, shared with the other Arbiter gaps.</summary>
    public const string DegradationCode = "arbiter-not-enforced";

    /// <summary>The Details prefix identifying this gap: the settings <c>hooks</c> key shadows the block.</summary>
    public const string Reason = "settings-hooks-shadowed";

    private const string ArbiterIdentity = "arbiter";

    /// <summary>Whether Squad's block would shadow hooks kept in <c>.factory/settings.json</c>.</summary>
    /// <remarks>
    /// Shadowed means exactly the hazard shape: <c>hooks.json</c> absent (so Factory
    /// falls back to <c>settings.json</c>) while that file carries a <c>hooks</c> key. A
    /// present <c>hooks.json</c> wins and the key is inert. An absent or unparsable
    /// <c>settings.json</c> is not proof of a <c>hooks</c> key, so the block renders —
    /// a file Factory itself cannot read cannot hold hooks it would lose.
    /// </remarks>
    public static bool IsShadowed(string targetRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetRoot);

        if (File.Exists(Path.Combine(targetRoot, HooksJsonRelativePath)))
        {
            return false;
        }

        string settingsPath = Path.Combine(targetRoot, SettingsJsonRelativePath);
        if (!File.Exists(settingsPath))
        {
            return false;
        }

        try
        {
            return JsonNode.Parse(File.ReadAllText(settingsPath)) is JsonObject settings
                && settings.ContainsKey(SettingsHooksKey);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>The degradation record for a dropped Factory block.</summary>
    /// <remarks>
    /// Target-scoped like the other Arbiter records: the identity fields carry the arbiter
    /// wiring itself and the instruction digest is empty — no instruction body is involved.
    /// The Details carry the reason plus the fix, since a finding that cannot be acted on
    /// is noise.
    /// </remarks>
    public static SquadDegradationRecord Degradation() => new(
        Target: FactoryTargetToken,
        CanonicalIdentity: ArbiterIdentity,
        OutputIdentity: ArbiterIdentity,
        Code: DegradationCode,
        InstructionDigest: string.Empty,
        Details: $"{Reason}: Factory reads {HooksJsonRelativePath} instead of the " +
            $"{SettingsHooksKey} key in {SettingsJsonRelativePath}; creating the file would " +
            "disable the user's own hooks. Move the user's hooks into .factory/hooks.json, " +
            "then run squad update.");

    /// <summary>
    /// Returns the blocks that may be spliced, dropping the Factory block when it would
    /// shadow the user's settings hooks, plus one degradation per drop.
    /// </summary>
    /// <remarks>
    /// Gated on the Arbiter being enabled at project scope: that is the only configuration
    /// under which a Factory block exists to drop, so a disabled wiring leaves both the
    /// blocks and the records untouched.
    /// </remarks>
    public static FactoryHooksShadowingOutcome Resolve(
        string targetRoot,
        IReadOnlyList<SquadTarget> targets,
        SquadArbiterWiring? arbiter,
        SquadDeploymentScope scope,
        IReadOnlyList<SquadRenderedBlock>? blocks)
    {
        ArgumentNullException.ThrowIfNull(targets);
        if (blocks is null || arbiter?.Enabled != true || scope != SquadDeploymentScope.Project)
        {
            return new FactoryHooksShadowingOutcome(blocks, []);
        }

        if (!targets.Contains(SquadTarget.Factory) || !IsShadowed(targetRoot))
        {
            return new FactoryHooksShadowingOutcome(blocks, []);
        }

        List<SquadRenderedBlock> kept = blocks
            .Where(block => !string.Equals(
                block.Target,
                FactoryTargetToken,
                StringComparison.Ordinal))
            .ToList();

        return new FactoryHooksShadowingOutcome(kept, [Degradation()]);
    }
}
